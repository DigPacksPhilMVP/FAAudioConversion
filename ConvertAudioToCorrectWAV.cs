using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

public static class AudioConverter
{
    [FunctionName("ConvertAudioToWav")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Audio conversion function triggered.");

        // Read and parse the HTTP request body
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);

        // Extract sourceBlobPath and targetBlobPath from payload
        if (payload == null || !payload.ContainsKey("sourceBlobPath") || !payload.ContainsKey("targetBlobPath"))
        {
            return new BadRequestObjectResult("The payload must contain 'sourceBlobPath' and 'targetBlobPath'.");
        }

        string sourceBlobPath = payload["sourceBlobPath"];
        string targetBlobPath = payload["targetBlobPath"];
        string storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        // Initialize BlobServiceClient
        var blobServiceClient = new BlobServiceClient(storageConnectionString);

        // Get the container client (assuming the blob path includes the container name)
        string containerName = sourceBlobPath.Split('/')[0];
        string blobName = sourceBlobPath.Substring(containerName.Length + 1);

        // Create a BlobContainerClient
        var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);

        // Create a BlobClient for the specific blob
        var sourceBlobClient = blobContainerClient.GetBlobClient(blobName);

        string tempSourcePath = Path.GetTempFileName();
        log.LogInformation($"Downloading blob from: {sourceBlobPath}");
        await sourceBlobClient.DownloadToAsync(tempSourcePath);

        // Temporary path for converted file
        string tempTargetPath = Path.ChangeExtension(Path.GetTempFileName(), ".wav");

        // Run FFmpeg
        string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg.exe");
        string arguments = $"-i \"{tempSourcePath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{tempTargetPath}\"";

        log.LogInformation($"Running FFmpeg with arguments: {arguments}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            log.LogError($"FFmpeg failed with error: {error}");
            return new BadRequestObjectResult($"FFmpeg conversion failed: {error}");
        }

        log.LogInformation("FFmpeg conversion succeeded.");

        // Extract container name and blob name from the targetBlobPath
        string targetContainerName = targetBlobPath.Split('/')[0];
        string targetBlobName = targetBlobPath.Substring(targetContainerName.Length + 1);

        // Get the container client for the target
        var targetBlobContainerClient = blobServiceClient.GetBlobContainerClient(targetContainerName);

        // Get the blob client for the target blob
        var targetBlobClient = targetBlobContainerClient.GetBlobClient(targetBlobName);

        // Upload the converted file to the target blob
        log.LogInformation($"Uploading converted file to: {targetBlobPath}");
        using (var stream = File.OpenRead(tempTargetPath))
        {
            await targetBlobClient.UploadAsync(stream, overwrite: true);
        }

        // Clean up temporary files
        File.Delete(tempSourcePath);
        File.Delete(tempTargetPath);

        log.LogInformation("Audio conversion completed successfully.");
        return new OkObjectResult($"Audio file converted and uploaded to {targetBlobPath}");
    }
}
