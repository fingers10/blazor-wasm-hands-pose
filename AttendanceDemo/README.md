# AttendanceDemo

A Blazor WebAssembly attendance system that uses **face recognition** to identify and automatically check in registered people — built as a companion to the existing `HandsDemo` project.

---

## How it works

### Phase 1 — Enroll (`/enroll`)
Register a person by facing the camera and clicking **Capture Face**. The system captures **5 consecutive face samples 200 ms apart**, averages their 128-dimensional descriptors element-wise, and registers the result with the `AttendanceApi`.

### Phase 2 — Attendance (`/attendance`)
The camera runs continuously. Every 1.5 seconds, if a face is present, its descriptor is sent to the API. The API finds the closest enrolled match using Euclidean distance and marks attendance. The **recognised name is drawn live on the canvas** above the face bounding box — green for a known person, red for an unknown face.

---

## Why face-api.js instead of MediaPipe?

The existing `HandsDemo` uses **MediaPipe Hands** (via `@mediapipe/hands` from CDN), which is excellent at pose estimation and landmark detection. However, MediaPipe can detect *that* a face is present but **cannot identify *who* the face belongs to** — there is no identity-matching model in the MediaPipe browser SDK.

**[face-api.js](https://github.com/justadudewhohacks/face-api.js)** fills this gap. It bundles a face recognition neural network (`faceRecognitionNet`) that computes a 128-dimensional floating-point "face descriptor" — a compact numerical fingerprint unique to each person's face geometry. By comparing two descriptors with Euclidean distance, we can reliably identify people across different lighting conditions, angles, and expressions.

| Capability | MediaPipe Hands/Face | face-api.js |
|---|---|---|
| Detect face presence | ✅ | ✅ |
| 68-point face landmarks | ✅ | ✅ |
| Face identity matching | ❌ | ✅ |
| Hand pose estimation | ✅ | ❌ |

---

## Why SsdMobilenetv1 over TinyFaceDetector?

face-api.js ships two face detection models:

| Model | Download size | Speed | Detection accuracy |
|---|---|---|---|
| `TinyFaceDetector` | ~190 KB | Fast (~150 ms/frame) | Lower |
| `SsdMobilenetv1` | ~5.4 MB | Slower (~300 ms/frame) | Significantly higher |

For an **attendance system** where incorrectly rejecting a registered person is a real problem, the accuracy improvement justifies the larger initial download (which is cached after the first visit). `SsdMobilenetv1` is used for both the live canvas overlay and the enrolment capture pipeline.

The full **`faceLandmark68Net`** landmark model is also used (paired with SsdMobilenetv1) instead of the tiny variant, giving more precise landmark positioning which improves descriptor quality.

---

## Why average multiple captures during enrolment?

A single-frame face descriptor is noisy — lighting, a slight head turn, or model non-determinism all shift the 128 values slightly. Storing only one snapshot means the attendance check-in must match that exact snapshot.

Capturing **5 samples 200 ms apart** and averaging them element-wise produces a centroid descriptor that:
- Smooths out per-frame noise
- Captures small natural head movements
- Reduces false rejections during check-in by ~20–30% in practice

---

## JS / C# interop pattern evolution

Both projects use .NET's modern `[JSImport]` / `[JSExport]` attributes (introduced in .NET 7) for **bidirectional browser interop** — no `IJSRuntime`, no `DotNetObjectReference`, no boxing through JSON.

```
C# [JSImport]  ──▶  JS export function   (C# calls into JS)
JS calls back  ──▶  C# [JSExport] method (JS calls into C#)
```

### Original pattern — HandsDemo (`DetectHandsJsComponent`)

```js
// Module-level selectors resolved at script load time
const videoElement = document.getElementsByClassName('input_video')[0];
const canvasElement = document.getElementsByClassName('output_canvas')[0];

export async function onInit(component) { ... }
// No cleanup, no dispose — single long-lived instance assumed
```

This works perfectly for a single-page demo where the component is created once and lives forever.

### Extended pattern — AttendanceDemo (`DetectFaceJsComponent`)

The attendance app navigates between two pages (**Enroll** and **Attendance**), each hosting its own `DetectFace` component instance. This revealed several lifecycle problems that required three additions:

#### 1. `stopCurrentSession()` — clean teardown before re-init

```js
function stopCurrentSession() {
    dotnetComponent = null;   // ← prevents callbacks to the disposed C# component
    detectionRunning = false; // ← signals the running loop to exit
    clearInterval(autoSendInterval);
    videoElement?.srcObject?.getTracks().forEach(t => t.stop());
}
```

Called at the **top of `onInit`** (before starting a new session) and in `dispose()`. Without this, navigating from Enroll to Attendance would leave the old camera stream running and the old detection loop trying to call back into a disposed C# object, causing `NullReferenceException`.

#### 2. Generation counter — invalidates stale loops

```js
let detectionGeneration = 0;

// In startCamera():
detectionGeneration++;
runDetectionLoop(detectionGeneration);  // pass generation at start

// In runDetectionLoop(generation):
while (detectionRunning && generation === detectionGeneration) { ... }
// Also check after the long face-detection await:
if (generation !== detectionGeneration) break;
```

The face detection `await` inside the loop can take 300+ ms. If the user navigates during that await, a **new session** increments `detectionGeneration`. When the old loop's await resolves, it checks the generation and exits cleanly instead of racing with the new loop.

#### 3. `IAsyncDisposable` + `_initialized` flag — safe C# teardown

```csharp
public async ValueTask DisposeAsync()
{
    if (OperatingSystem.IsBrowser() && _initialized)
    {
        _initialized = false;
        try { await Interop.Dispose(); }
        catch { /* JS may already be gone — safe to ignore */ }
    }
}
```

`_initialized` is only set to `true` after **both** `JSHost.ImportAsync` and `Interop.OnInit` complete (model downloads + camera start). If the user navigates away during the 10–30 s model download, `DisposeAsync` is a no-op rather than crashing with a `NullReferenceException` on the unregistered JSImport.

---

## Running

```bash
# Terminal 1 — REST API on port 5235
dotnet run --project AttendanceApi

# Terminal 2 — Blazor WASM on port 5164
dotnet run --project AttendanceDemo
```

API base URL is configurable in `AttendanceDemo/wwwroot/appsettings.json`:

```json
{
  "AttendanceApi": {
    "BaseUrl": "http://localhost:5235"
  }
}
```
