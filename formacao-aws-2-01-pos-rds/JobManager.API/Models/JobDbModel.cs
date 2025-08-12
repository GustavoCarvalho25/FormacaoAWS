using Amazon.DynamoDBv2.DataModel;
using JobManager.API.Entities;

namespace JobManager.API.Models;

[DynamoDBTable("Jobs")]
public class JobDbModel
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public decimal MinSalary { get; set; }
    public decimal MaxSalary { get; set; }
    public string Company { get; set; }
    public List<JobApplicationDbModel> Applications { get; set; }

    public static JobDbModel FromEntity(Job job)
        => new JobDbModel
        {
            Id = Guid.NewGuid().ToString(),
            Title = job.Title,
            Description = job.Description,
            MinSalary = job.MinSalary,
            MaxSalary = job.MaxSalary,
            Company = job.Company,
            Applications = []
        };
}


public class JobApplicationDbModel
{
    public string Id { get; set; }
    public string CandidateName { get; set; }
    public string CandidateEmail { get; set; }
    public string? CVUrl { get; set; }
}