using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Components;

namespace FaceMeshJsComponent
{
    public partial class FaceMesh : IAsyncDisposable
    {
        private bool _initialized;

        [Parameter] public string Mode { get; set; } = "visualize";
        [Parameter] public string LivenessMode { get; set; } = "none";
        [Parameter] public EventCallback<FeatureVectorResult> OnFeatureVectorCaptured { get; set; }

        public bool LivenessConfirmed { get; set; }
        public bool DepthConfirmed { get; set; }     // sub-flag for 'both' mode
        public bool FaceDetected { get; set; }
        public bool IsCapturing { get; private set; }
        public FeatureVectorResult? LatestVector { get; set; }

        public void SetLivenessMode(string mode)
        {
            if (OperatingSystem.IsBrowser() && _initialized)
                Interop.SetLivenessMode(mode);
        }

        public void ResetLiveness()
        {
            if (OperatingSystem.IsBrowser() && _initialized)
                Interop.ResetLiveness();
        }

        protected override async Task OnInitializedAsync()
        {
            if (OperatingSystem.IsBrowser())
            {
                await JSHost.ImportAsync(
                    "FaceMeshJsComponent/FaceMesh",
                    "../_content/FaceMeshJsComponent/FaceMesh.razor.js");
                try
                {
                    await Interop.OnInit(this, Mode, LivenessMode);
                    _initialized = true;
                }
                catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("Permission denied"))
                {
                    // Camera permission denied — JS updated the loading message; swallow here.
                }
            }
        }

        public async Task CaptureFeatureVector()
        {
            if (OperatingSystem.IsBrowser() && _initialized && !IsCapturing)
            {
                IsCapturing = true;
                StateHasChanged();
                await Interop.CaptureFeatureVector(this);
                // IsCapturing is reset inside OnFeatureVectorReady
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (OperatingSystem.IsBrowser() && _initialized)
            {
                _initialized = false;
                try { await Interop.Dispose(); }
                catch (Exception ex) { Console.WriteLine($"FaceMesh.DisposeAsync: {ex.Message}"); }
            }
        }

        [SupportedOSPlatform("browser")]
        public partial class Interop
        {
            [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(JsonTypeInfo))]
            static Interop() { }

            [JSImport("onInit", "FaceMeshJsComponent/FaceMesh")]
            internal static partial Task OnInit(
                [JSMarshalAs<JSType.Any>] object component,
                string mode,
                string livenessMode);

            [JSImport("captureFeatureVector", "FaceMeshJsComponent/FaceMesh")]
            internal static partial Task CaptureFeatureVector(
                [JSMarshalAs<JSType.Any>] object component);

            [JSImport("setLivenessMode", "FaceMeshJsComponent/FaceMesh")]
            internal static partial void SetLivenessMode(string mode);

            [JSImport("resetLiveness", "FaceMeshJsComponent/FaceMesh")]
            internal static partial void ResetLiveness();

            [JSImport("dispose", "FaceMeshJsComponent/FaceMesh")]
            internal static partial Task Dispose();

            // Called in visualize mode only — full landmark JSON
            [JSExport]
            internal static void OnResults(
                [JSMarshalAs<JSType.Any>] object component, string json)
            {
                var faceMesh = (FaceMesh)component;
                faceMesh.DetectionResult = JsonSerializer.Deserialize<FaceMeshResult>(
                    json, FaceMeshResult.SerializeOptions);
                faceMesh.StateHasChanged();
            }

            // Called in enroll/checkin modes with the 956-float identity vector
            [JSExport]
            internal static void OnFeatureVectorReady(
                [JSMarshalAs<JSType.Any>] object component, string json)
            {
                var faceMesh = (FaceMesh)component;
                faceMesh.IsCapturing = false;
                faceMesh.LatestVector = JsonSerializer.Deserialize<FeatureVectorResult>(
                    json, FeatureVectorResult.SerializeOptions);
                _ = faceMesh.OnFeatureVectorCaptured.InvokeAsync(faceMesh.LatestVector!);
                faceMesh.StateHasChanged();
            }

            [JSExport]
            internal static void OnDepthChanged(
                [JSMarshalAs<JSType.Any>] object component, bool confirmed)
            {
                var faceMesh = (FaceMesh)component;
                if (faceMesh.DepthConfirmed != confirmed)
                {
                    faceMesh.DepthConfirmed = confirmed;
                    faceMesh.StateHasChanged();
                }
            }

            [JSExport]
            internal static void OnLivenessChanged(
                [JSMarshalAs<JSType.Any>] object component, bool confirmed)
            {
                var faceMesh = (FaceMesh)component;
                if (faceMesh.LivenessConfirmed != confirmed)
                {
                    faceMesh.LivenessConfirmed = confirmed;
                    faceMesh.StateHasChanged();
                }
            }

            [JSExport]
            internal static void OnFaceStatusChanged(
                [JSMarshalAs<JSType.Any>] object component, bool faceDetected)
            {
                var faceMesh = (FaceMesh)component;
                if (faceMesh.FaceDetected != faceDetected)
                {
                    faceMesh.FaceDetected = faceDetected;
                    faceMesh.StateHasChanged();
                }
            }
        }
    }

    public record FeatureVectorResult
    {
        internal static readonly JsonSerializerOptions SerializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public bool Detected { get; set; }
        public float[]? Vector { get; set; }
        public int SampleCount { get; set; }
    }

    public record FaceMeshResult
    {
        internal static readonly JsonSerializerOptions SerializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public List<Face> Faces { get; set; } = [];
    }

    public record Face
    {
        public int Index { get; set; }
        public List<Landmark> Landmarks { get; set; } = [];
    }

    public record Landmark
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }
}
