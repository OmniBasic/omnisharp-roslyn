using System;
using System.Collections.Generic;
using System.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Endpoint.Exports;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Models.UpdateBuffer;
using OmniSharp.Plugins;
using OmniSharp.Stdio.Protocol;

namespace OmniSharp.Endpoint
{
    public abstract class EndpointHandler
    {
        public abstract Task<object> Handle(RequestPacket context);

        public static EndpointHandler Create<TRequest, TResponse>(IPredicateHandler languagePredicateHandler, CompositionHost host,
            ILogger logger,
            OmniSharpEndpointMetadata metadata,
            IEnumerable<Lazy<IRequestHandler, OmniSharpRequestHandlerMetadata>> handlers,
            Lazy<EndpointHandler<UpdateBufferRequest, object>> updateBufferHandler,
            IEnumerable<Plugin> plugins)
        {
            return new EndpointHandler<TRequest, TResponse>(languagePredicateHandler, host, logger, metadata, handlers.Where(x => x.Metadata.EndpointName == metadata.EndpointName), updateBufferHandler, plugins);
        }

        public static EndpointHandler Factory(IPredicateHandler languagePredicateHandler, CompositionHost host,
            ILogger logger,
            OmniSharpEndpointMetadata metadata,
            IEnumerable<Lazy<IRequestHandler, OmniSharpRequestHandlerMetadata>> handlers,
            Lazy<EndpointHandler<UpdateBufferRequest, object>> updateBufferHandler,
            IEnumerable<Plugin> plugins)
        {
            var createMethod = typeof(EndpointHandler).GetTypeInfo().DeclaredMethods.First(x => x.Name == nameof(EndpointHandler.Create));
            return (EndpointHandler)createMethod.MakeGenericMethod(metadata.RequestType, metadata.ResponseType).Invoke(null, new object[] { languagePredicateHandler, host, logger, metadata, handlers, updateBufferHandler, plugins });
        }
    }

    public class EndpointHandler<TRequest, TResponse> : EndpointHandler
    {
        private readonly CompositionHost _host;
        private readonly IPredicateHandler _languagePredicateHandler;
        private readonly Lazy<Task<Dictionary<string, ExportHandler<TRequest, TResponse>>>> _exports;
        private readonly OmniSharpWorkspace _workspace;
        private readonly bool _hasLanguageProperty;
        private readonly bool _hasFileNameProperty;
        private readonly bool _canBeAggregated;
        private readonly ILogger _logger;
        private readonly IEnumerable<Plugin> _plugins;
        private readonly Lazy<EndpointHandler<UpdateBufferRequest, object>> _updateBufferHandler;

        public EndpointHandler(IPredicateHandler languagePredicateHandler, CompositionHost host, ILogger logger, OmniSharpEndpointMetadata metadata, IEnumerable<Lazy<IRequestHandler, OmniSharpRequestHandlerMetadata>> handlers, Lazy<EndpointHandler<UpdateBufferRequest, object>> updateBufferHandler, IEnumerable<Plugin> plugins)
        {
            EndpointName = metadata.EndpointName;
            _host = host;
            _logger = logger;
            _languagePredicateHandler = languagePredicateHandler;
            _plugins = plugins;
            _workspace = host.GetExport<OmniSharpWorkspace>();

            _hasLanguageProperty = metadata.RequestType.GetRuntimeProperty(nameof(LanguageModel.Language)) != null;
            _hasFileNameProperty = metadata.RequestType.GetRuntimeProperty(nameof(Request.FileName)) != null;
            _canBeAggregated = typeof(IAggregateResponse).IsAssignableFrom(metadata.ResponseType);
            _updateBufferHandler = updateBufferHandler;

            _exports = new Lazy<Task<Dictionary<string, ExportHandler<TRequest, TResponse>>>>(() => LoadExportHandlers(handlers));
        }

        private Task<Dictionary<string, ExportHandler<TRequest, TResponse>>> LoadExportHandlers(IEnumerable<Lazy<IRequestHandler, OmniSharpRequestHandlerMetadata>> handlers)
        {
            var interfaceHandlers = handlers
                .Select(export => new RequestHandlerExportHandler<TRequest, TResponse>(export.Metadata.Language, (IRequestHandler<TRequest, TResponse>)export.Value))
                .Cast<ExportHandler<TRequest, TResponse>>();

            var plugins = _plugins.Where(x => x.Config.Endpoints.Contains(EndpointName))
                .Select(plugin => new PluginExportHandler<TRequest, TResponse>(EndpointName, plugin))
                .Cast<ExportHandler<TRequest, TResponse>>();

            return Task.FromResult(interfaceHandlers
               .Concat(plugins)
               .ToDictionary(export => export.Language));
        }

        public string EndpointName { get; }

        public override Task<object> Handle(RequestPacket context)
        {
            var requestObject = DeserializeRequestObject(context.ArgumentsStream);
            var model = GetLanguageModel(requestObject);

            return Process(context, model, requestObject);
        }

        public async Task<object> Process(RequestPacket context, LanguageModel model, JToken requestObject)
        {
            var request = requestObject.ToObject<TRequest>();
            if (request is Request && _updateBufferHandler.Value != null)
            {
                var realRequest = request as Request;
                if (!string.IsNullOrWhiteSpace(realRequest.FileName) && (realRequest.Buffer != null || realRequest.Changes != null))
                {
                    await _updateBufferHandler.Value.Process(context, model, requestObject);
                }
            }

            if (_hasLanguageProperty)
            {
                // Handle cases where a request isn't aggrgate and a language isn't specified.
                // This helps with editors calling a legacy end point, for example /metadata
                if (!_canBeAggregated && string.IsNullOrWhiteSpace(model.Language))
                {
                    model.Language = LanguageNames.CSharp;
                }
                return await HandleLanguageRequest(model.Language, request, context);
            }
            else if (_hasFileNameProperty)
            {
                var language = _languagePredicateHandler.GetLanguageForFilePath(model.FileName ?? string.Empty);
                return await HandleLanguageRequest(language, request, context);
            }
            else
            {
                var language = _languagePredicateHandler.GetLanguageForFilePath(string.Empty);
                if (!string.IsNullOrEmpty(language))
                {
                    return await HandleLanguageRequest(language, request, context);
                }
            }

            return await HandleAllRequest(request, context);
        }

        private Task<object> HandleLanguageRequest(string language, TRequest request, RequestPacket packet)
        {
            if (!string.IsNullOrEmpty(language))
            {
                return HandleSingleRequest(language, request, packet);
            }

            return HandleAllRequest(request, packet);
        }

        private async Task<object> HandleSingleRequest(string language, TRequest request, RequestPacket packet)
        {
            var exports = await _exports.Value;
            if (exports.TryGetValue(language, out var handler))
            {
                return await handler.Handle(request);
            }

            throw new NotSupportedException($"{language} does not support {EndpointName}");
        }

        private async Task<object> HandleAllRequest(TRequest request, RequestPacket packet)
        {
            if (!_canBeAggregated)
            {
                throw new NotSupportedException($"Must be able aggregate the response to spread them out across all plugins for {EndpointName}");
            }

            var exports = await _exports.Value;

            IAggregateResponse aggregateResponse = null;

            var responses = new List<Task<TResponse>>();
            foreach (var handler in exports.Values)
            {
                responses.Add(handler.Handle(request));
            }

            foreach (IAggregateResponse exportResponse in await Task.WhenAll(responses))
            {
                if (aggregateResponse != null)
                {
                    aggregateResponse = aggregateResponse.Merge(exportResponse);
                }
                else
                {
                    aggregateResponse = exportResponse;
                }
            }

            object response = aggregateResponse;

            if (response != null)
            {
                return response;
            }

            return null;
        }

        private LanguageModel GetLanguageModel(JToken jtoken)
        {
            var response = new LanguageModel();
            var jobject = jtoken as JObject;
            if (jobject == null)
            {
                return response;
            }

            if (jobject.TryGetValue(nameof(LanguageModel.Language), StringComparison.OrdinalIgnoreCase, out var token))
            {
                response.Language = token.ToString();
            }


            if (jobject.TryGetValue(nameof(LanguageModel.FileName), StringComparison.OrdinalIgnoreCase, out token))
            {
                response.FileName = token.ToString();
            }

            return response;
        }

        private JToken DeserializeRequestObject(Stream readStream)
        {
            try
            {
                using (var streamReader = new StreamReader(readStream))
                {
                    using (var textReader = new JsonTextReader(streamReader))
                    {
                        return JToken.Load(textReader);
                    }
                }
            }
            catch
            {
                return new JObject();
            }
        }
    }
}
