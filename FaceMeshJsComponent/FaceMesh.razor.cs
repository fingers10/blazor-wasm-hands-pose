using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Runtime.Versioning;

namespace FaceMeshJsComponent
{
    public partial class FaceMesh
    {
        protected override async Task OnInitializedAsync()
        {
            if (OperatingSystem.IsBrowser())
            {
                await JSHost.ImportAsync(
                    "FaceMeshJsComponent/FaceMesh",
                    "../_content/FaceMeshJsComponent/FaceMesh.razor.js");

                await Interop.OnInit(this);
            }
        }

        [SupportedOSPlatform("browser")]
        public partial class Interop
        {
            // [JSImport] — Blazor calls into JavaScript
            [JSImport("onInit", "FaceMeshJsComponent/FaceMesh")]
            internal static partial Task OnInit([JSMarshalAs<JSType.Any>] object component);

            // [JSExport] — JavaScript calls back into Blazor
            [JSExport]
            internal static void OnResults([JSMarshalAs<JSType.Any>] object component, string json)
            {
                var faceMesh = (FaceMesh)component;
                faceMesh.DetectionResult = JsonSerializer.Deserialize<FaceMeshResult>(
                    json, FaceMeshResult.SerializeOptions);
                faceMesh.StateHasChanged();
            }
        }
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
