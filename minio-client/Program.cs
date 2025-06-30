using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Net;

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

            // Add services to the container.
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // Register Minio client ONCE with all settings
            builder.Services.AddMinio(configureClient => configureClient
                .WithEndpoint(endpoint)
                .WithCredentials(accessKey, secretKey)
                .WithSSL(secure)
                .Build());

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI();

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

        [HttpPost]
        public async Task<IActionResult> UploadFile(IFormFile file)
        {
            var bucketName = "test-bucket";
            // Allowed content types
            var allowedContentTypes = new[] { "image/png", "image/jpeg", "video/mp4", "video/x-msvideo" };
            var allowedExtensions = new[] { ".png", ".jpg", ".jpeg", ".mp4", ".avi" };

            // Check content type
            if (!allowedContentTypes.Contains(file.ContentType.ToLower()))
            {
                return BadRequest(new { error = "Invalid file type. Only PNG and JPEG images are allowed." });
            }

            // Check file extension
            var fileExtension = Path.GetExtension(file.FileName).ToLower();
            if (!allowedExtensions.Contains(fileExtension))
            {
                return BadRequest(new { error = "Invalid file extension. Only .png, .jpg, and .jpeg are allowed." });
            }

            try
            {
                // เช็กว่า bucket มีอยู่หรือยัง
                var bucketExistsArgs = new BucketExistsArgs()
                    .WithBucket(bucketName);
                bool found = await minioClient.BucketExistsAsync(bucketExistsArgs);
                if (!found)
                {
                    var makeBucketArgs = new MakeBucketArgs()
                        .WithBucket(bucketName);
                    await minioClient.MakeBucketAsync(makeBucketArgs);
                }

                // อัปโหลดไฟล์ไปยัง bucket
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
    }

    [ApiController]
    [Route("api/[controller]")]
    public class ExampleFactoryController : ControllerBase
    {
        private readonly IMinioClientFactory minioClientFactory;

        public ExampleFactoryController(IMinioClientFactory minioClientFactory)
        {
            this.minioClientFactory = minioClientFactory;
        }

        [HttpGet]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetUrl(string bucketID)
        {
            var minioClient = minioClientFactory.CreateClient(); //Has optional argument to configure specifics

            return Ok(await minioClient.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                    .WithBucket(bucketID))
                .ConfigureAwait(false));
        }
    }
}
