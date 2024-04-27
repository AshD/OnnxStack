﻿using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxStack.Core;
using OnnxStack.Core.Model;
using OnnxStack.StableDiffusion.Common;
using OnnxStack.StableDiffusion.Config;
using OnnxStack.StableDiffusion.Enums;
using OnnxStack.StableDiffusion.Models;
using OnnxStack.StableDiffusion.Schedulers.StableDiffusion;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OnnxStack.StableDiffusion.Diffusers.StableCascade
{
    public abstract class StableCascadeDiffuser : DiffuserBase
    {
        private readonly UNetConditionModel _decoderUnet;

        /// <summary>
        /// Initializes a new instance of the <see cref="StableCascadeDiffuser"/> class.
        /// </summary>
        /// <param name="priorUnet">The prior unet.</param>
        /// <param name="decoderUnet">The decoder unet.</param>
        /// <param name="decoderVqgan">The decoder vqgan.</param>
        /// <param name="imageEncoder">The image encoder.</param>
        /// <param name="memoryMode">The memory mode.</param>
        /// <param name="logger">The logger.</param>
        public StableCascadeDiffuser(UNetConditionModel priorUnet, UNetConditionModel decoderUnet, AutoEncoderModel decoderVqgan, AutoEncoderModel imageEncoder, MemoryModeType memoryMode, ILogger logger = default)
            : base(priorUnet, decoderVqgan, imageEncoder, memoryMode, logger)
        {
            _decoderUnet = decoderUnet;
        }

        /// <summary>
        /// Gets the type of the pipeline.
        /// </summary>
        public override DiffuserPipelineType PipelineType => DiffuserPipelineType.StableCascade;


        /// <summary>
        /// Runs the scheduler steps.
        /// </summary>
        /// <param name="promptOptions">The prompt options.</param>
        /// <param name="schedulerOptions">The scheduler options.</param>
        /// <param name="promptEmbeddings">The prompt embeddings.</param>
        /// <param name="performGuidance">if set to <c>true</c> [perform guidance].</param>
        /// <param name="progressCallback">The progress callback.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public override async Task<DenseTensor<float>> DiffuseAsync(PromptOptions promptOptions, SchedulerOptions schedulerOptions, PromptEmbeddingsResult promptEmbeddings, bool performGuidance, Action<DiffusionProgress> progressCallback = null, CancellationToken cancellationToken = default)
        {
            // Prior Unet
            var latentsPrior = await DiffusePriorAsync(schedulerOptions, promptEmbeddings, performGuidance, progressCallback, cancellationToken);

            // Decoder Unet
            var schedulerOptionsDecoder = schedulerOptions with { InferenceSteps = 10, GuidanceScale = 0 };
            var latents = await DiffuseDecodeAsync(latentsPrior, schedulerOptionsDecoder, promptEmbeddings, performGuidance, progressCallback, cancellationToken);

            // Decode Latents
            return await DecodeLatentsAsync(promptOptions, schedulerOptions, latents);
        }


        protected async Task<DenseTensor<float>> DiffusePriorAsync(SchedulerOptions schedulerOptions, PromptEmbeddingsResult promptEmbeddings, bool performGuidance, Action<DiffusionProgress> progressCallback = null, CancellationToken cancellationToken = default)
        {
            using (var scheduler = GetScheduler(schedulerOptions))
            {
                // Get timesteps
                var timesteps = GetTimesteps(schedulerOptions, scheduler);

                // Create latent sample
                var latents = scheduler.CreateRandomSample(new[] { 1, 16, (int)Math.Ceiling(schedulerOptions.Height / 42.67f), (int)Math.Ceiling(schedulerOptions.Width / 42.67f) }, scheduler.InitNoiseSigma);

                // Get Model metadata
                var metadata = await _unet.GetMetadataAsync();

                // Loop though the timesteps
                var step = 0;
                foreach (var timestep in timesteps)
                {
                    step++;
                    var stepTime = Stopwatch.GetTimestamp();
                    cancellationToken.ThrowIfCancellationRequested();

                    // Create input tensor.
                    var inputLatent = performGuidance ? latents.Repeat(2) : latents;
                    var inputTensor = scheduler.ScaleInput(inputLatent, timestep);
                    var timestepTensor = CreateTimestepTensor(inputLatent, timestep);
                    var imageEmbeds = new DenseTensor<float>(new[] { performGuidance ? 2 : 1, 1, 768 });

                    var outputChannels = performGuidance ? 2 : 1;
                    var outputDimension = inputTensor.Dimensions.ToArray();
                    using (var inferenceParameters = new OnnxInferenceParameters(metadata))
                    {
                        inferenceParameters.AddInputTensor(inputTensor);
                        inferenceParameters.AddInputTensor(timestepTensor);
                        inferenceParameters.AddInputTensor(promptEmbeddings.PooledPromptEmbeds);
                        inferenceParameters.AddInputTensor(promptEmbeddings.PromptEmbeds);
                        inferenceParameters.AddInputTensor(imageEmbeds);
                        inferenceParameters.AddOutputBuffer(outputDimension);

                        var results = await _unet.RunInferenceAsync(inferenceParameters);
                        using (var result = results.First())
                        {
                            var noisePred = result.ToDenseTensor();

                            // Perform guidance
                            if (performGuidance)
                                noisePred = PerformGuidance(noisePred, schedulerOptions.GuidanceScale);

                            // Scheduler Step
                            latents = scheduler.Step(noisePred, timestep, latents).Result;
                        }
                    }

                    ReportProgress(progressCallback, step, timesteps.Count, latents);
                    _logger?.LogEnd(LogLevel.Debug, $"Prior Step {step}/{timesteps.Count}", stepTime);
                }

                // Unload if required
                if (_memoryMode == MemoryModeType.Minimum)
                    await _unet.UnloadAsync();

                return latents;
            }
        }


        protected async Task<DenseTensor<float>> DiffuseDecodeAsync(DenseTensor<float> latentsPrior, SchedulerOptions schedulerOptions, PromptEmbeddingsResult promptEmbeddings, bool performGuidance, Action<DiffusionProgress> progressCallback = null, CancellationToken cancellationToken = default)
        {
            using (var scheduler = GetScheduler(schedulerOptions))
            {
                // Get timesteps
                var timesteps = GetTimesteps(schedulerOptions, scheduler);

                // Create latent sample
                var latents = scheduler.CreateRandomSample(new[] { 1, 4, (int)(latentsPrior.Dimensions[2] * 10.67f), (int)(latentsPrior.Dimensions[3] * 10.67f) }, scheduler.InitNoiseSigma);

                // Get Model metadata
                var metadata = await _decoderUnet.GetMetadataAsync();

                var effnet = performGuidance
                    ? latentsPrior
                    : latentsPrior.Concatenate(new DenseTensor<float>(latentsPrior.Dimensions));


                // Loop though the timesteps
                var step = 0;
                foreach (var timestep in timesteps)
                {
                    step++;
                    var stepTime = Stopwatch.GetTimestamp();
                    cancellationToken.ThrowIfCancellationRequested();

                    // Create input tensor.
                    var inputLatent = performGuidance ? latents.Repeat(2) : latents;
                    var inputTensor = scheduler.ScaleInput(inputLatent, timestep);
                    var timestepTensor = CreateTimestepTensor(inputLatent, timestep);

                    var outputChannels = performGuidance ? 2 : 1;
                    var outputDimension = inputTensor.Dimensions.ToArray(); //schedulerOptions.GetScaledDimension(outputChannels);
                    using (var inferenceParameters = new OnnxInferenceParameters(metadata))
                    {
                        inferenceParameters.AddInputTensor(inputTensor);
                        inferenceParameters.AddInputTensor(timestepTensor);
                        inferenceParameters.AddInputTensor(promptEmbeddings.PooledPromptEmbeds);
                        inferenceParameters.AddInputTensor(effnet);
                        inferenceParameters.AddOutputBuffer();

                        var results = _decoderUnet.RunInference(inferenceParameters);
                        using (var result = results.First())
                        {
                            var noisePred = result.ToDenseTensor();

                            // Perform guidance
                            if (performGuidance)
                                noisePred = PerformGuidance(noisePred, schedulerOptions.GuidanceScale);

                            // Scheduler Step
                            latents = scheduler.Step(noisePred, timestep, latents).Result;
                        }
                    }

                    ReportProgress(progressCallback, step, timesteps.Count, latents);
                    _logger?.LogEnd(LogLevel.Debug, $"Decoder Step {step}/{timesteps.Count}", stepTime);
                }

                // Unload if required
                if (_memoryMode == MemoryModeType.Minimum)
                    await _unet.UnloadAsync();

                return latents;
            }
        }


        protected override async Task<DenseTensor<float>> DecodeLatentsAsync(PromptOptions prompt, SchedulerOptions options, DenseTensor<float> latents)
        {
            latents = latents.MultiplyBy(_vaeDecoder.ScaleFactor);

            var outputDim = new[] { 1, 3, options.Height, options.Width };
            var metadata = await _vaeDecoder.GetMetadataAsync();
            using (var inferenceParameters = new OnnxInferenceParameters(metadata))
            {
                inferenceParameters.AddInputTensor(latents);
                inferenceParameters.AddOutputBuffer(outputDim);

                var results = await _vaeDecoder.RunInferenceAsync(inferenceParameters);
                using (var imageResult = results.First())
                {
                    // Unload if required
                    if (_memoryMode == MemoryModeType.Minimum)
                        await _vaeDecoder.UnloadAsync();

                    return imageResult
                        .ToArray()
                        .AsSpan()
                        .NormalizeOneToOne()
                        .ToDenseTensor(outputDim);
                }
            }
        }


        /// <summary>
        /// Creates the timestep tensor.
        /// </summary>
        /// <param name="latents">The latents.</param>
        /// <param name="timestep">The timestep.</param>
        /// <returns></returns>
        private DenseTensor<float> CreateTimestepTensor(DenseTensor<float> latents, int timestep)
        {
            var timestepTensor = new DenseTensor<float>(new[] { latents.Dimensions[0] });
            timestepTensor.Fill(timestep / 1000f);
            return timestepTensor;
        }


        /// <summary>
        /// Gets the scheduler.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns></returns>
        protected override IScheduler GetScheduler(SchedulerOptions options)
        {
            return options.SchedulerType switch
            {
                SchedulerType.DDPM => new DDPMScheduler(options),
                _ => default
            };
        }
    }
}
