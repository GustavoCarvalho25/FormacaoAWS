using Amazon;
using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Amazon.S3.Model;
using JobManager.API.Entities;
using JobManager.API.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.

        builder.Configuration.AddSystemsManager(source =>
        {
            source.AwsOptions = new AWSOptions
            {
                Region = RegionEndpoint.SAEast1
            };
            source.Path = "/";
            source.ReloadAfter = TimeSpan.FromMinutes(30);
        });

        // builder.Configuration.AddSecretsManager(null, RegionEndpoint.SAEast1, config =>
        // {
        //     config.KeyGenerator = (secret, name) => name.Replace("/", ":");
        //     config.PollingInterval = TimeSpan.FromMinutes(30);
        // });
        // aí seria necessário adicionar a chave no Secrets Manager na AWS
        
        var connectionString = builder.Configuration.GetConnectionString("AppDb");

        builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();

        app.MapPost("/api/jobs", async (Job job, AppDbContext db) =>
        {
            await db.Jobs.AddAsync(job);
            await db.SaveChangesAsync();

            return Results.Created($"/api/jobs/{job.Id}", job);
        });

        app.MapGet("/api/jobs/{id}", async (int id, AppDbContext db) =>
        {
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id);

            if (job is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(job);
        });

        app.MapGet("/api/jobs", async (AppDbContext db) =>
        {
            var jobs = await db.Jobs.ToListAsync();

            return Results.Ok(jobs);
        });

        app.MapPost("/api/jobs/{id}/job-applications", async (int id, JobApplication application, [FromServices] AppDbContext db) =>
        {
            var exists = await db.Jobs.AnyAsync(j => j.Id == id);

            if (!exists)
            {
                return Results.NotFound();
            }

            application.JobId = id;

            await db.JobApplications.AddAsync(application);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        app.MapPut("/api/job-applications/{id}/upload-cv", async (int id, IFormFile file, [FromServices] AppDbContext db) =>
        {
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest();
            }

            var extension = Path.GetExtension(file.FileName);

            var validExtensions = new List<string> { ".pdf", ".docx" };

            if (!validExtensions.Contains(extension))
            {
                return Results.BadRequest();
            }
            
            var client = new AmazonS3Client(RegionEndpoint.SAEast1);

            var bucketName = "formacaoawsgustavot9";
            
            var key = $"job-applications/{id}-{file.FileName}";

            using var stream = file.OpenReadStream();

            var putObject = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = stream
            };
            
            var response = await client.PutObjectAsync(putObject);

            var application = await db.JobApplications.SingleOrDefaultAsync(ja => ja.Id == id);

            if (application is null)
            {
                return Results.NotFound();
            }

            application.CVUrl = key;

            await db.SaveChangesAsync();

            return Results.NoContent();
        }).DisableAntiforgery();

        app.MapGet("/api/job-applications/{id}/cv", async (int id, string email, [FromServices] AppDbContext db) =>
        {
            var baseS3Url =
                "https://formacaoawsgustavot9.s3.sa-east-1.amazonaws.com/job-applications/";
            
            var application = await db.JobApplications.SingleOrDefaultAsync(ja => ja.CandidateEmail == email);

            var fullKey = $"{baseS3Url}/{id}-{application.CVUrl}";
            
            var bucketName = "formacaoawsgustavot9";

            var getRequest = new GetObjectRequest
            {
                BucketName = bucketName,
                Key = fullKey
            };
            
            var client = new AmazonS3Client(RegionEndpoint.SAEast1);
            
            var response = await client.GetObjectAsync(getRequest);
            
            return Results.File(response.ResponseStream, response.Headers.ContentType, response.Headers.ContentDisposition);
        });
        app.Run();
    }
}