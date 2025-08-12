using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using JobManager.API.Entities;
using JobManager.API.Models;
using JobManager.API.Persistence;
using JobManager.API.Workers;
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

        builder.Services.AddHostedService<JobApplicationNotificationWorker>();
        
        var dynamoDbClient = new AmazonDynamoDBClient(RegionEndpoint.SAEast1);
        
        builder.Services.AddSingleton<IAmazonDynamoDB>(dynamoDbClient);
        
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

        app.MapPost("/api/jobs/{id}/job-applications", async (int id, JobApplication application, [FromServices] AppDbContext db,
           [FromServices] IConfiguration configuration) =>
        {
            var exists = await db.Jobs.AnyAsync(j => j.Id == id);

            if (!exists)
            {
                return Results.NotFound();
            }

            application.JobId = id;

            await db.JobApplications.AddAsync(application);
            
            
            var client = new AmazonSQSClient(RegionEndpoint.SAEast1);
            
            var queueUrl = configuration.GetValue<string>("AWS:SQSQueueUrl") ?? string.Empty;
            
            var queueResponse = await client.GetQueueUrlAsync("FormacaoAwsGustavoC");
            
            var queue = queueResponse.QueueUrl;
            
            var request = new SendMessageRequest
            {
                QueueUrl = queue,
                MessageBody = $"New application for job {id} by {application.CandidateName} ({application.CandidateEmail})"
            };
            
            var result = await client.SendMessageAsync(request);
            
            await db.SaveChangesAsync();
            
            return Results.NoContent();
        });

        app.MapPut("/api/job-applications/{id}/upload-cv", async (int id, IFormFile file, [FromServices] AppDbContext db, [FromServices] IConfiguration configuration) =>
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

            var bucketName = configuration.GetValue<string>("AWS:S3BucketName");
            
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

        app.MapGet("/api/job-applications/{id}/cv", async (int id, string email, [FromServices] AppDbContext db, [FromServices] IConfiguration configuration) =>
        {
            var application = await db.JobApplications.SingleOrDefaultAsync(ja => ja.CandidateEmail == email);

            //var fullKey = $"{baseS3Url}/{id}-{application.CVUrl}";
            
            var bucketName = configuration.GetValue<string>("AWS:S3BucketName");

            var getRequest = new GetObjectRequest
            {
                BucketName = bucketName,
                Key = application.CVUrl
            };
            
            var client = new AmazonS3Client(RegionEndpoint.SAEast1);
            
            var response = await client.GetObjectAsync(getRequest);
            
            return Results.File(response.ResponseStream, response.Headers.ContentType, response.Headers.ContentDisposition);
        });
        
        
        
        //---------------------------------------------------------------- [ API - V2 ] ----------------------------------------------------------------
        
        app.MapPost("/api/v2/jobs", async (Job job) =>
        {
            var db = new DynamoDBContext(dynamoDbClient);

            var model = JobDbModel.FromEntity(job);
            
            await db.SaveAsync(model);

            return Results.Created($"/api/jobs/{job.Id}", job);
        });
        
        app.MapGet("/api/v2/jobs", async () =>
        {
            var db = new DynamoDBContext(dynamoDbClient);
            
            var jobs = await db.ScanAsync<JobDbModel>([]).GetRemainingAsync();

            return Results.Ok(jobs);
        });
        
        app.MapGet("/api/v2/jobs/{id}", async (string id) =>
        {
            var db = new DynamoDBContext(dynamoDbClient);
            
            var job = await db.LoadAsync<JobDbModel>(id);

            if (job is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(job);
        });
        
        app.MapPost("/api/v2/jobs/{id}/job-applications", async (int id, JobApplication application, [FromServices] IConfiguration configuration) =>
        {
            var db = new DynamoDBContext(dynamoDbClient);
            
            var job = await db.LoadAsync<JobDbModel>(id.ToString());

            if (job is null)
            {
                return Results.NotFound();
            }

            application.JobId = id;

            var jobApplicationModel = new JobApplicationDbModel
            {
                Id = Guid.NewGuid().ToString(),
                CandidateName = application.CandidateName,
                CandidateEmail = application.CandidateEmail,
                CVUrl = application.CVUrl,
            };
            
            
            job.Applications.Add(jobApplicationModel);

            await db.SaveAsync(job);
            
            var client = new AmazonSQSClient(RegionEndpoint.SAEast1);
            
            var queueUrl = configuration.GetValue<string>("AWS:SQSQueueUrl") ?? string.Empty;
            
            var queueResponse = await client.GetQueueUrlAsync("FormacaoAwsGustavoC");
            
            var queue = queueResponse.QueueUrl;
            
            var request = new SendMessageRequest
            {
                QueueUrl = queue,
                MessageBody = $"New application for job {id} by {application.CandidateName} ({application.CandidateEmail})"
            };
            
            var result = await client.SendMessageAsync(request);
            
            return Results.NoContent();
        });
        
        app.MapPut("/api/v2/job/{id}/job-applications/{applicationId}/upload-cv", async (string id, string applicationId, IFormFile file, [FromServices] IConfiguration configuration) =>
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

            var bucketName = configuration.GetValue<string>("AWS:S3BucketName");
            
            var key = $"job-applications/{applicationId}-{file.FileName}";

            using var stream = file.OpenReadStream();

            var putObject = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = stream
            };
            
            var db = new DynamoDBContext(dynamoDbClient);

            
            var response = await client.PutObjectAsync(putObject);

            var job = await db.LoadAsync<JobDbModel>(id);
            
            var application = job.Applications.SingleOrDefault(a => a.Id == applicationId);

            if (application is null)
            {
                return Results.NotFound();
            }

            application.CVUrl = key;

            await db.SaveAsync(job);

            return Results.NoContent();
        }).DisableAntiforgery();
        
        app.Run();
    }
}