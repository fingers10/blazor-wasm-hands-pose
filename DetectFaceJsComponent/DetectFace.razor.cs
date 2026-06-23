using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace DetectFaceJsComponent
{
    public partial class DetectFace : IAsyncDisposable
    {
        private bool _initialized;

        protected override async Task OnInitializedAsync()
        {
            if (OperatingSystem.IsBrowser())
            {
                await JSHost.ImportAsync(
                    "DetectFaceJsComponent/DetectFace",
                    "../_content/DetectFaceJsComponent/DetectFace.razor.js");
                await Interop.OnInit(this, Mode);
                _initialized = true;
            }
        }

        public async Task CaptureDescriptor()
        {
            if (OperatingSystem.IsBrowser() && _initialized)
            {
                await Interop.CaptureDescriptor(this);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (OperatingSystem.IsBrowser() && _initialized)
            {
                _initialized = false;
                try
                {
                    await Interop.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DetectFace.DisposeAsync: JS cleanup failed (safe to ignore): {ex.Message}");
                }
            }
        }

        [SupportedOSPlatform("browser")]
        public partial class Interop
        {
            [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(JsonTypeInfo))]
            static Interop()
            {
            }

            [JSImport("onInit", "DetectFaceJsComponent/DetectFace")]
            internal static partial Task OnInit(
                [JSMarshalAs<JSType.Any>] object component,
                string mode);

            [JSImport("captureDescriptor", "DetectFaceJsComponent/DetectFace")]
            internal static partial Task CaptureDescriptor(
                [JSMarshalAs<JSType.Any>] object component);

            [JSImport("dispose", "DetectFaceJsComponent/DetectFace")]
            internal static partial Task Dispose();

            [JSExport]
            internal static void OnDescriptorReady(
                [JSMarshalAs<JSType.Any>] object component,
                string json)
            {
                DetectFace detectFace = (DetectFace)component;
                detectFace.LatestDetection = JsonSerializer.Deserialize<DescriptorResult>(
                    json, DescriptorResult.SerializeOptions);
                _ = detectFace.OnDescriptorCaptured.InvokeAsync(detectFace.LatestDetection!);
                detectFace.StateHasChanged();
            }

            [JSExport]
            internal static void OnFaceStatusChanged(
                [JSMarshalAs<JSType.Any>] object component,
                bool faceDetected)
            {
                DetectFace detectFace = (DetectFace)component;
                if (detectFace.FaceDetected != faceDetected)
                {
                    detectFace.FaceDetected = faceDetected;
                    detectFace.StateHasChanged();
                }
            }
        }
    }

    public record DescriptorResult
    {
        internal static readonly JsonSerializerOptions SerializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public bool Detected { get; set; }
        public float[]? Descriptor { get; set; }
    }
}
