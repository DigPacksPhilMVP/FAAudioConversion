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

        try
        {
            // Parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);

            if (payload == null || !payload.ContainsKey("sourceBlobUrl") || !payload.ContainsKey("targetBlobPath"))
            {
                return new BadRequestObjectResult("The payload must contain 'sourceBlobUrl' and 'targetBlobPath'.");
            }

            string sourceBlobUrl = payload["sourceBlobUrl"];
            string targetBlobPath = payload["targetBlobPath"];

            log.LogInformation($"Source Blob URL: {sourceBlobUrl}");
            log.LogInformation($"Target Blob Path: {targetBlobPath}");

            // Initialize BlobClient for the source blob using its URL
            var sourceBlobClient = new BlobClient(new Uri(sourceBlobUrl));

            // Download the source blob to a temporary path
            string tempSourcePath = Path.GetTempFileName();
            log.LogInformation($"Downloading blob from URL: {sourceBlobUrl}");
            await sourceBlobClient.DownloadToAsync(tempSourcePath);

            // Temporary path for the converted file
            string tempTargetPath = Path.ChangeExtension(Path.GetTempFileName(), ".wav");
            log.LogInformation($"Temporary target path: {tempTargetPath}");

            // Run FFmpeg
            string ffmpegPath = @"C:\home\site\wwwroot\tools\ffmpeg.exe";
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

            log.LogInformation($"FFmpeg output: {output}");
            log.LogInformation($"FFmpeg error output: {error}");
            log.LogInformation($"FFmpeg exited with code: {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                log.LogError($"FFmpeg failed with exit code {process.ExitCode}: {error}");
                return new BadRequestObjectResult($"FFmpeg conversion failed: {error}");
            }

            log.LogInformation("FFmpeg conversion succeeded.");

            // Use the connection string for the sa1812240914 storage account
            var blobServiceClient = new BlobServiceClient(Environment.GetEnvironmentVariable("SA1812240914_ConnectionString"));
            string targetContainerName = targetBlobPath.Split('/')[0];
            string targetBlobName = targetBlobPath.Substring(targetContainerName.Length + 1);
            var targetBlobContainerClient = blobServiceClient.GetBlobContainerClient(targetContainerName);

            // Check if the container exists, and create it if it doesn't
            if (!await targetBlobContainerClient.ExistsAsync())
            {
                log.LogWarning($"Container '{targetContainerName}' does not exist. Creating it.");
                await targetBlobContainerClient.CreateIfNotExistsAsync();
                log.LogInformation($"Container '{targetContainerName}' created.");
            }

            var targetBlobClient = targetBlobContainerClient.GetBlobClient(targetBlobName);

            log.LogInformation($"Uploading converted file to: {targetBlobPath}");
            using (var stream = File.OpenRead(tempTargetPath))
            {
                await targetBlobClient.UploadAsync(stream, overwrite: true);
            }

            log.LogInformation($"File uploaded successfully to {targetBlobPath}");

            // Clean up temporary files
            File.Delete(tempSourcePath);
            File.Delete(tempTargetPath);

            log.LogInformation("Audio conversion completed successfully.");
            string targetBlobUri = targetBlobClient.Uri.ToString();
            log.LogInformation($"Converted file URI: {targetBlobUri}");
            return new OkObjectResult(new { message = "Audio file converted successfully", fileUri = targetBlobUri });
        }
        catch (Exception ex)
        {
            log.LogError($"Error during audio conversion: {ex.Message}");
            log.LogError($"Stack Trace: {ex.StackTrace}");
            return new ObjectResult($"An error occurred: {ex.Message}") { StatusCode = 500 };
        }
    }
}
