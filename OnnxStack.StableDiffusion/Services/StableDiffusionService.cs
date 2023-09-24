﻿using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxStack.StableDiffusion.Common;
using OnnxStack.StableDiffusion.Config;
using OnnxStack.StableDiffusion.Results;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Threading.Tasks;

namespace OnnxStack.StableDiffusion.Services
{
    public sealed class StableDiffusionService : IStableDiffusionService
    {
        private readonly IInferenceService _inferenceService;

        public StableDiffusionService(IInferenceService inferenceService)
        {
            _inferenceService = inferenceService;
        }

        public Task<ImageResult> TextToImage(StableDiffusionOptions options)
        {
            return TextToImageInternal(options, new SchedulerOptions());
        }

        public Task<ImageResult> TextToImage(StableDiffusionOptions options, SchedulerOptions schedulerOptions)
        {
            return TextToImageInternal(options, schedulerOptions);
        }

        public Task<ImageResult> TextToImageFile(StableDiffusionOptions options, string outputFile)
        {
            return TextToImageFileInternal(options, new SchedulerOptions(), outputFile);
        }

        public Task<ImageResult> TextToImageFile(StableDiffusionOptions options, SchedulerOptions schedulerOptions, string outputFile)
        {
            return TextToImageFileInternal(options, schedulerOptions, outputFile);
        }


        private async Task<ImageResult> TextToImageInternal(StableDiffusionOptions options, SchedulerOptions schedulerConfig)
        {
            var imageTensorData = await _inferenceService.RunInferenceAsync(options, schedulerConfig).ConfigureAwait(false);
            return TensorToImage(options, imageTensorData);
        }

        private async Task<ImageResult> TextToImageFileInternal(StableDiffusionOptions options, SchedulerOptions schedulerConfig, string outputFile)
        {
            var result = await TextToImageInternal(options, schedulerConfig);
            if (result is null)
                return null;

            await result.SaveAsync(outputFile);
            return result;
        }


        private ImageResult TensorToImage(StableDiffusionOptions options, DenseTensor<float> imageTensor)
        {
            var result = new Image<Rgba32>(options.Width, options.Height);
            for (var y = 0; y < options.Height; y++)
            {
                for (var x = 0; x < options.Width; x++)
                {
                    result[x, y] = new Rgba32(
                        CalculateByte(imageTensor, 0, y, x),
                        CalculateByte(imageTensor, 1, y, x),
                        CalculateByte(imageTensor, 2, y, x)
                    );
                }
            }
            return new ImageResult(result);
        }

        private static byte CalculateByte(Tensor<float> imageTensor, int index, int y, int x)
        {
            return (byte)Math.Round(Math.Clamp(imageTensor[0, index, y, x] / 2 + 0.5, 0, 1) * 255);
        }
    }
}
