﻿using Microsoft.Extensions.DependencyInjection;
using OnnxStack.Core.Config;
using OnnxStack.Core.Services;
using OnnxStack.StableDiffusion.Common;
using OnnxStack.StableDiffusion.Config;
using OnnxStack.StableDiffusion.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;

namespace OnnxStack.Core
{
    /// <summary>
    /// .NET Core Service and Dependancy Injection registration helpers
    /// </summary>
    public static class Registration
    {
        /// <summary>
        /// Register OnnxStack StableDiffusion services
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        public static void AddOnnxStackStableDiffusion(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddOnnxStack();
            serviceCollection.RegisterServices();
            serviceCollection.AddSingleton(TryLoadAppSettings());
        }


        /// <summary>
        /// Register OnnxStack StableDiffusion services, AddOnnxStack() must be called before
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="configuration">The configuration.</param>
        public static void AddOnnxStackStableDiffusion(this IServiceCollection serviceCollection, StableDiffusionConfig configuration)
        {
            serviceCollection.RegisterServices();
            serviceCollection.AddSingleton(configuration);
        }


        private static void RegisterServices(this IServiceCollection serviceCollection)
        {
            ConfigureLibraries();

            // Services
            serviceCollection.AddSingleton<IVideoService, VideoService>();
            serviceCollection.AddSingleton<IStableDiffusionService, StableDiffusionService>();
        }


        /// <summary>
        /// Configures any 3rd party libraries.
        /// </summary>
        private static void ConfigureLibraries()
        {
            // Create a 100MB image buffer pool
            Configuration.Default.PreferContiguousImageBuffers = true;
            Configuration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                MaximumPoolSizeMegabytes = 100,
            });
        }


        /// <summary>
        /// Try load StableDiffusionConfig from application settings.
        /// </summary>
        /// <returns></returns>
        private static StableDiffusionConfig TryLoadAppSettings()
        {
            try
            {
                return ConfigManager.LoadConfiguration<StableDiffusionConfig>();
            }
            catch
            {
                return new StableDiffusionConfig();
            }
        }
    }
}
