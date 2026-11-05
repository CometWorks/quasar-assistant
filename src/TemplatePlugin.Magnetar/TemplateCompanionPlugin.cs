using System;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Sandbox.Game.World;
using VRage.Plugins;

namespace TemplatePlugin.Magnetar
{
    public sealed class TemplateCompanionPlugin : IPlugin, IQuasarCompanionRequestHandler
    {
        private const string RuntimeInfoOperation = "runtime.info";

        public string PluginId => "todo.template-plugin";

        public void Init(object gameServer)
        {
        }

        public void Update()
        {
        }

        public void Dispose()
        {
        }

        public Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(
            CompanionPluginRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(request.Operation, RuntimeInfoOperation, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CompanionPluginResponse
                {
                    SchemaVersion = request.SchemaVersion,
                    CorrelationId = request.CorrelationId,
                    Success = false,
                    Error = $"Unsupported template companion operation '{request.Operation}'.",
                });
            }

            var assemblyVersion = typeof(MySession).Assembly.GetName().Version?.ToString() ?? "unknown";
            return Task.FromResult(new CompanionPluginResponse
            {
                SchemaVersion = request.SchemaVersion,
                CorrelationId = request.CorrelationId,
                Success = true,
                PayloadJson = $"{{\"spaceEngineersAssemblyVersion\":\"{assemblyVersion}\"}}",
            });
        }
    }
}
