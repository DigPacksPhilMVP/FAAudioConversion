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

        // Extract sourceBlobUrl and targetBlobPath from payload
        if (payload == null || !payload.ContainsKey("sourceBlobUrl") || !payload.ContainsKey("targetBlobPath"))
        {
            return new BadRequestObjectResult("The payload must contain 'sourceBlobUrl' and 'targetBlobPath'.");
        }

        string sourceBlobUrl = payload["sourceBlobUrl"];
        string targetBlobPath = payload["targetBlobPath"]; // This is the relative path, e.g., "convertedpacks/katiesteve_converted.wav"

        try
        {
            // Initialize BlobClient for the source blob using the full URL
            var sourceBlobClient = new BlobClient(new Uri(sourceBlobUrl));

            // Download the source blob to a temporary path
            string tempSourcePath = Path.GetTempFileName();
            log.LogInformation($"Downloading blob from URL: {sourceBlobUrl}");
            await sourceBlobClient.DownloadToAsync(tempSourcePath);

            // Temporary path for the converted file
            string tempTargetPath = Path.ChangeExtension(Path.GetTempFileName(), ".wav");

            // Run FFmpeg
            string ffmpegPath = Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg.exe");
            string arguments = $"-i \"{tempSourcePath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{tempTargetPath}\"";

            log.LogInformation($"FFmpeg path: {ffmpegPath}");
            log.LogInformation($"FFmpeg arguments: {arguments}");
            log.LogInformation($"Input file path: {tempSourcePath}");
            log.LogInformation($"Output file path: {tempTargetPath}");

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

            // Construct the full URI for the target blob in the same storage account
            Uri sourceBlobUri = new Uri(sourceBlobUrl);
            string targetBlobUri = $"{sourceBlobUri.Scheme}://{sourceBlobUri.Host}/{targetBlobPath}";
            log.LogInformation($"Target blob full URI: {targetBlobUri}");

            // Initialize BlobClient for the target blob
            var targetBlobClient = new BlobClient(new Uri(targetBlobUri));

            // Upload the converted file to the target blob
            log.LogInformation($"Uploading converted file to: {targetBlobUri}");
            using (var stream = File.OpenRead(tempTargetPath))
            {
                await targetBlobClient.UploadAsync(stream, overwrite: true);
            }

            // Clean up temporary files
            File.Delete(tempSourcePath);
            File.Delete(tempTargetPath);

            log.LogInformation("Audio conversion completed successfully.");
            return new OkObjectResult($"Audio file converted and uploaded to {targetBlobUri}");
        }
        catch (Exception ex)
        {
            log.LogError($"Error during audio conversion: {ex.Message}");
            log.LogError($"Stack Trace: {ex.StackTrace}");
            return new ObjectResult($"An error occurred: {ex.Message}") { StatusCode = 500 };
        }
    }
}
