using AspNetCore.Unobtrusive.Ajax;
using OnnxStack.Core;
using OnnxStack.Web.Hubs;
using Services;
using System.Text.Json.Serialization;

namespace OnnxStack.WebUI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorPages();
            builder.Services.AddUnobtrusiveAjax();
            builder.Services.AddSignalR()
                .AddJsonProtocol(options =>
                {
                    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });

            builder.Services.AddOnnxStackStableDiffusion();
            builder.Services.AddSingleton<IFileService, FileService>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }
            app.UseStaticFiles();

            app.UseUnobtrusiveAjax();

            app.UseRouting();

            app.UseAuthorization();

            app.MapRazorPages();

            app.MapHub<StableDiffusionHub>(nameof(StableDiffusionHub));

            app.Run();
        }
    }
}