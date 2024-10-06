using OS = System;
using System.Collections.Generic;


public class BackendHost
{
    public string host;
    public string? ipaddr;
    public int port;
    public string protocol;
    public string probe_path;

    string? _url = null;
    string? _probeurl = null;
    public string url => _url ??= new UriBuilder(protocol, ipaddr ?? host, port).Uri.AbsoluteUri;

    public string probeurl => _probeurl ??= System.Net.WebUtility.UrlDecode( new UriBuilder(protocol, ipaddr ?? host, port, probe_path).Uri.AbsoluteUri );

    private const int MaxData = 50;
    private readonly Queue<double> latencies = new Queue<double>();
    private readonly Queue<bool> callSuccess = new Queue<bool>();

    private Queue<double> PxLatency = new Queue<double>();
    private int errors=0;
    private object lockObj = new object();

    public BackendHost(string hostname, string? probepath, string? ipaddress)
    {


        // If host does not have a protocol, add one
        if (!hostname.StartsWith("http://") && !hostname.StartsWith("https://"))
        {
            hostname = "https://" + hostname;
        }

        // if host ends with a slash, remove it
        if (hostname.EndsWith("/"))
        {
            hostname = hostname.Substring(0, hostname.Length - 1);
        }

        // parse the host, prototol and port
        Uri uri = new Uri(hostname);
        protocol = uri.Scheme;
        port = uri.Port;
        host = uri.Host;

        probe_path = probepath ?? "echo/resource?param1=sample";
        if (probe_path.StartsWith("/"))
        {
            probe_path = probe_path.Substring(1);
        }

        // Uncomment UNTIL sslStream is implemented
        // if (ipaddress != null)
        // {
        //     // Valudate that the address is in the right format
        //     if (!System.Net.IPAddress.TryParse(ipaddress, out _))
        //     {
        //         throw new System.UriFormatException($"Invalid IP address: {ipaddress}");
        //     }
        //     ipaddr = ipaddress;
        // }


        Console.WriteLine($"Adding backend host: {this.host}  probe path: {this.probe_path}");
    }
    public override string ToString()
    {
        return $"{protocol}://{host}:{port}";
    }

    public void AddPxLatency(double latency)
    {
        lock(lockObj) {
            PxLatency.Enqueue(latency);
        }
    }

    public void AddError() {
        lock(lockObj) {
            errors++;
        }
    }

    public string GetStatus(out int calls, out int errorCalls, out double average)
    {
        if (PxLatency.Count == 0)
        {
            errorCalls = errors;
            average = 0;
            calls = 0;

            // Reset the error count
            errors = 0;

            return " - ";
        }

        var status=PxLatency;
        errorCalls=errors;
        lock (lockObj)
        {
            // Reset the counts
            PxLatency = new Queue<double>();
            errors = 0;
        }

        average = Math.Round(status.Average(), 3);
        calls = status.Count;

        return $" Calls: {status.Count} Err: {errorCalls} Avg: {Math.Round(status.Average(), 3)}ms";
    }

    // Method to add a new latency
    public void AddLatency(double latency)
    {
        // If there are already 50 latencies in the queue, remove the oldest one
        if (latencies.Count == MaxData)
            latencies.Dequeue();

        // Add the new latency to the queue
        latencies.Enqueue(latency);
    }
    // Method to calculate the average latency
    public double AverageLatency()
    {
        // If there are no latencies, return 0.0
        if (latencies.Count == 0)
            return 0.0;

        // Otherwise, return the average latency + penalty for each failed call
        return latencies.Average() + (1 - SuccessRate()) * 100;
    }

    // Method to track the success of a call
    public void AddCallSuccess(bool success)
    {
        // If there are already 50 call results in the queue, remove the oldest one
        if (callSuccess.Count == MaxData)
            callSuccess.Dequeue();

        // Add the new call result to the queue
        callSuccess.Enqueue(success);
    }

    // Method to calculate the success rate
    public double SuccessRate()
    {
        // If there are no call results, return 0.0
        if (callSuccess.Count == 0)
            return 0.0;

        // Otherwise, return the success rate
        return (double)callSuccess.Count(x => x) / callSuccess.Count;
    }
}