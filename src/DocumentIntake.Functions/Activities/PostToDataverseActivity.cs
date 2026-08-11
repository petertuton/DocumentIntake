using DocumentIntake.Functions.Models;
using DocumentIntake.Functions.Services;
using Microsoft.Azure.Functions.Worker;

namespace DocumentIntake.Functions.Activities;

public sealed class PostToDataverseActivity
{
    public const string Name = nameof(PostToDataverseActivity);

    private readonly IDataverseClient _client;

    public PostToDataverseActivity(IDataverseClient client) => _client = client;

    [Function(Name)]
    public Task<string?> RunAsync([ActivityTrigger] MappedForm form)
        => _client.CreateRecordAsync(form);
}
