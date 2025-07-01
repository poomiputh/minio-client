using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace minio_client
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var endpoint = "127.0.0.1:9000";
            var accessKey = "ROOTUSER";
            var secretKey = "minioAdmin123";
            var secure = false;

            ServicePointManager.ServerCertificateValidationCallback +=
            (sender, certificate, chain, sslPolicyErrors) => true;

            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });

            // Add services to the container.
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // Increase multipart body length limit to 6 GB
            builder.Services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = 6L * 1024 * 1024 * 1024; // 6 GB
            });

            // Register Minio client ONCE with all settings
            builder.Services.AddMinio(configureClient => configureClient
                .WithEndpoint(endpoint)
                .WithCredentials(accessKey, secretKey)
                .WithSSL(secure)
                .Build());

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI();

            app.UseCors();

            app.MapControllers();

            app.Run();
        }
    }

    [ApiController]
    [Route("api/[controller]/[action]")]
    public class ExampleController : ControllerBase
    {
        private readonly IMinioClient minioClient;

        public ExampleController(IMinioClient minioClient)
        {
            this.minioClient = minioClient;
        }

        [HttpGet]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetUrl(string bucketID)
        {
            return Ok(await minioClient.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                    .WithBucket(bucketID))
                .ConfigureAwait(false));
        }

        [HttpGet]
        public async Task<IActionResult> GetBucketList()
        {
            return Ok(await minioClient.ListBucketsAsync().ConfigureAwait(false));
        }

        [RequestSizeLimit(6L * 1024 * 1024 * 1024)] // Set file size limit to 6GB
        [HttpPost]
        public async Task<IActionResult> UploadFile(IFormFile file)
        {
            var bucketName = "test-bucket";
            var maxFileSize = 5L * 1024 * 1024 * 1024; // 5 GB

            var allowedContentTypes = new[] { "image/png", "image/jpeg", "video/mp4", "video/x-msvideo" };
            var allowedExtensions = new[] { ".png", ".jpg", ".jpeg", ".mp4", ".avi" };

            if (file.Length > maxFileSize)
            {
                return BadRequest(new { error = "File size exceeds the 5 GB limit." });
            }

            if (!allowedContentTypes.Contains(file.ContentType.ToLower()))
            {
                return BadRequest(new { error = "Invalid file type." });
            }

            var fileExtension = Path.GetExtension(file.FileName).ToLower();
            if (!allowedExtensions.Contains(fileExtension))
            {
                return BadRequest(new { error = "Invalid file extension." });
            }

            try
            {
                var bucketExistsArgs = new BucketExistsArgs().WithBucket(bucketName);
                bool found = await minioClient.BucketExistsAsync(bucketExistsArgs);
                if (!found)
                {
                    var makeBucketArgs = new MakeBucketArgs().WithBucket(bucketName);
                    await minioClient.MakeBucketAsync(makeBucketArgs);
                }

                var putObjectArgs = new PutObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(file.FileName)
                    .WithStreamData(file.OpenReadStream())
                    .WithObjectSize(file.Length)
                    .WithContentType(file.ContentType);

                await minioClient.PutObjectAsync(putObjectArgs);
                Console.WriteLine("Successfully uploaded: " + file.FileName);

                return Ok(new { message = "Upload successful", file = file.FileName });
            }
            catch (MinioException e)
            {
                Console.WriteLine("File Upload Error: {0}", e.Message);
                return StatusCode(500, new { error = e.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DownloadFile(string fileName)
        {
            var bucketName = "test-bucket";

            try
            {
                MemoryStream memoryStream = new MemoryStream();

                var getObjectArgs = new GetObjectArgs()
                                        .WithBucket(bucketName)
                                        .WithObject(fileName)
                                        .WithCallbackStream(stream =>
                                        {
                                            stream.CopyTo(memoryStream);
                                        });

                await minioClient.GetObjectAsync(getObjectArgs);

                memoryStream.Seek(0, SeekOrigin.Begin); // กลับไปจุดเริ่มของ stream

                // ส่งกลับเป็น File
                return File(memoryStream, "application/octet-stream", fileName);
            }
            catch (MinioException e)
            {
                Console.WriteLine("File Download Error: {0}", e.Message);
                return StatusCode(500, new { error = e.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DownloadFileStream(string fileName)
        {
            var bucketName = "test-bucket";

            try
            {
                var stat = await minioClient
                    .StatObjectAsync(new StatObjectArgs().WithBucket(bucketName).WithObject(fileName));

                Response.ContentLength = stat.Size;
                Response.ContentType = stat.ContentType ?? "application/octet-stream";
                Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");

                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(fileName)
                    .WithCallbackStream(async (stream, cancellationToken) =>
                    {
                        await stream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
                    });

                await minioClient.GetObjectAsync(getObjectArgs);

                return new EmptyResult(); // Response already written
            }
            catch (MinioException e)
            {
                Console.WriteLine("File Download Error: {0}", e.Message);
                return StatusCode(500, new { error = e.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> StreamVideo(string fileName)
        {
            var bucketName = "test-bucket";
            try
            {
                // Get file info (e.g., size, content type)
                var statObjectArgs = new StatObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(fileName);
                var stat = await minioClient.StatObjectAsync(statObjectArgs);

                var contentType = stat.ContentType ?? "application/octet-stream";
                var fileLength = stat.Size;

                // Check for Range header
                var rangeHeader = Request.Headers["Range"].ToString();
                long start = 0, end = fileLength - 1;
                bool isPartial = false;

                // If have range header and range header start with "bytes="
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    isPartial = true;
                    var range = rangeHeader.Replace("bytes=", "").Split('-');
                    start = long.Parse(range[0]);
                    // if specify end for range
                    if (range.Length > 1 && !string.IsNullOrEmpty(range[1]))
                        end = long.Parse(range[1]);
                }

                var length = end - start + 1;

                Response.StatusCode = isPartial ? 206 : 200;
                Response.ContentType = contentType;
                Response.ContentLength = length;
                if (isPartial)
                {
                    Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileLength}");
                    Response.Headers.Append("Accept-Ranges", "bytes");
                }

                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(fileName)
                    .WithOffsetAndLength(start, length)
                    .WithCallbackStream(async (stream, cancellationToken) =>
                    {
                        await stream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
                        stream.Dispose();
                    });

                await minioClient.GetObjectAsync(getObjectArgs);

                return new EmptyResult(); // Response already written
            }
            catch (MinioException e)
            {
                Console.WriteLine("Video Stream Error: {0}", e.Message);
                return StatusCode(500, new { error = e.Message });
            }
        }
    }

    //[ApiController]
    //[Route("api/[controller]")]
    //public class ExampleFactoryController : ControllerBase
    //{
    //    private readonly IMinioClientFactory minioClientFactory;

    //    public ExampleFactoryController(IMinioClientFactory minioClientFactory)
    //    {
    //        this.minioClientFactory = minioClientFactory;
    //    }

    //    [HttpGet]
    //    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    //    public async Task<IActionResult> GetUrl(string bucketID)
    //    {
    //        var minioClient = minioClientFactory.CreateClient(); //Has optional argument to configure specifics

    //        return Ok(await minioClient.PresignedGetObjectAsync(new PresignedGetObjectArgs()
    //                .WithBucket(bucketID))
    //            .ConfigureAwait(false));
    //    }
    //}
}
