using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace JobManager.API.Workers;

public class JobApplicationNotificationWorker : BackgroundService
{
    readonly IConfiguration _configuration;

    public JobApplicationNotificationWorker(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = new AmazonSQSClient(RegionEndpoint.SAEast1);
        
        var queueUrl = _configuration.GetValue<string>("AWS:SQSQueueUrl");

        while (!stoppingToken.IsCancellationRequested)
        {
            var request = new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 20
            };
            
            var response = await client.ReceiveMessageAsync(request, stoppingToken);

            if (response.HttpStatusCode == System.Net.HttpStatusCode.OK)
            {
                foreach (var message in response.Messages)
                {
                    Console.WriteLine($"Message: {message.Body}");
                    
                    await client.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
                }
            }
        }
    }
}