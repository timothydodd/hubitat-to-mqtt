using System.Dynamic;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MQTTnet;
using MQTTnet.Formatter;

/// <summary>
/// Represents a health check, which can be used to check the status of the mqtt server.
/// </summary>
public class MqttHealthCheck : IHealthCheck
{

    public IMqttClient UnManagedMqttClient { get; private set; }



    public MqttHealthCheck(IMqttClient client)
    {

        UnManagedMqttClient = client ?? throw new ArgumentNullException(nameof(client));
    }


    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {

        return CheckUnManagedMqttClient(context, cancellationToken);

    }

    private async Task<HealthCheckResult> CheckUnManagedMqttClient(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (UnManagedMqttClient.IsConnected)
            {
                await UnManagedMqttClient.PingAsync(cancellationToken);
                return HealthCheckResult.Healthy(PrintProtocolVersion(UnManagedMqttClient.Options.ProtocolVersion));
            }
            return new HealthCheckResult(context.Registration.FailureStatus, "Could not connect to the broker");
        }
        catch (Exception e)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, HandleExceptions(e));
        }
    }


    private string HandleExceptions(Exception e)
    {
        if (e.Message == "A task was canceled.")
            return "Could not connect to the broker";

        return e.Message;
    }

    private string PrintProtocolVersion(MqttProtocolVersion version)
    {
        switch (version)
        {
            case MqttProtocolVersion.V310:
                return "Protocol version 3.1.0";
            case MqttProtocolVersion.V311:
                return "Protocol version 3.1.1";
            case MqttProtocolVersion.V500:
                return "Protocol version 5.0.0";
            default:
                return "Protocol version 'unknown'";
        }
    }
}
public static class MqttHealthCheckBuilderExtensions
{
    /// <summary>
    /// Add a health check for mqtt services 
    /// </summary>
    /// <param name="builder">The <see cref="IHealthChecksBuilder"/>.</param>
    /// <param name="client">Client that will be used to connect to the mqtt server to verify if it's state is OK</param>
    /// <param name="options">Options that need to be used to connect to the server if not yet connected</param>
    /// <param name="name">The health check name. Optional. If <c>null</c> the type name 'Mqtt' will be used for the name.</param>
    /// <param name="failureStatus">The <see cref="HealthStatus"/> that should be reported when the health check fails. Optional. If <c>null</c> then
    /// the default status of <see cref="HealthStatus.Unhealthy"/> will be reported.</param>
    /// <param name="tags">A list of tags that can be used to filter sets of health checks. Optional.</param>
    /// <param name="timeout">An optional System.TimeSpan representing the timeout of the check.</param>
    /// <returns>The <see cref="IHealthChecksBuilder"/>.</returns>
    public static IHealthChecksBuilder AddMqtt(this IHealthChecksBuilder builder, string name = "Mqtt", HealthStatus failureStatus = HealthStatus.Unhealthy, IEnumerable<string>? tags = default, TimeSpan? timeout = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentNullException(nameof(name));

        builder.Services.AddSingleton<MqttHealthCheck>();

        return builder.Add(new HealthCheckRegistration(name, sp => sp.GetRequiredService<MqttHealthCheck>(),
            failureStatus,
            tags,
            timeout));
    }
}
public static class HealthCheck
{


    public static Task WriteResponse(HttpContext httpContext,
        HealthReport result)
    {
        httpContext.Response.ContentType = "application/json";
        var hc = new
        {
            status = result.Status.ToString(),
            results = GetResultsExpando(result)
        };


        return httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(hc, new JsonSerializerOptions()
            {
                WriteIndented = true
            }));

        static ExpandoObject GetResultsExpando(HealthReport result)
        {
            dynamic resultExpando = new ExpandoObject();
            foreach (var entry in result.Entries)
            {
                AddProperty(resultExpando, entry.Key, new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    data = GetDataExpando(entry.Value.Data)
                });
            }
            return resultExpando;
        }

        static ExpandoObject GetDataExpando(IReadOnlyDictionary<string, object> data)
        {
            dynamic dataExpando = new ExpandoObject();
            foreach (var d in data)
            {
                AddProperty(dataExpando, d.Key, d.Value);
            }

            return dataExpando;
        }

        static bool AddProperty(ExpandoObject obj, string key, object value)
        {
            var dynamicDict = obj as IDictionary<string, object>;
            if (dynamicDict.ContainsKey(key))
                return false;
            else
                dynamicDict.Add(key, value);
            return true;
        }

    }


}
