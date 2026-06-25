// MediaPipe Face Mesh — Blazor JSImport/JSExport interop module
// Mirrors DetectHands.razor.js pattern: CDN script injection → camera → onResults → Blazor callback

const mpFaceMesh = window;
const drawingUtils = window;
const videoElement = document.getElementsByClassName('input_video')[0];
const canvasElement = document.getElementsByClassName('output_canvas')[0];
const canvasCtx = canvasElement.getContext('2d');

/** Dynamically inject a <script> tag and wait for it to load */
function insertGlobalScript(url) {
    const element = document.createElement('script');
    element.setAttribute('src', url);
    element.setAttribute('crossorigin', 'anonymous');
    document.head.appendChild(element);
    return new Promise((resolve) => { element.onload = resolve; });
}

let detectFaceMeshExports;

export async function onInit(component) {
    // Grab the .NET assembly exports so JS can call back into Blazor
    const { getAssemblyExports } = await globalThis.getDotnetRuntime(0);
    detectFaceMeshExports = await getAssemblyExports('FaceMeshJsComponent.dll');

    // Load MediaPipe libraries from CDN (same pattern as DetectHands.razor.js)
    await insertGlobalScript('https://cdn.jsdelivr.net/npm/@mediapipe/camera_utils@latest/camera_utils.js');
    await insertGlobalScript('https://cdn.jsdelivr.net/npm/@mediapipe/drawing_utils@latest/drawing_utils.js');
    await insertGlobalScript('https://cdn.jsdelivr.net/npm/@mediapipe/face_mesh@latest/face_mesh.js');

    // Create the FaceMesh detector
    const faceMesh = new mpFaceMesh.FaceMesh({
        locateFile: (file) => {
            return `https://cdn.jsdelivr.net/npm/@mediapipe/face_mesh@latest/${file}`;
        }
    });

    faceMesh.setOptions({
        selfieMode:             true,
        maxNumFaces:            2,
        refineLandmarks:        true,   // adds 10 iris landmarks per eye (478 total per face)
        minDetectionConfidence: 0.5,
        minTrackingConfidence:  0.5
    });

    faceMesh.onResults(results => onResults(component, results));

    // Start the webcam feed
    const camera = new Camera(videoElement, {
        onFrame: async () => {
            await faceMesh.send({ image: videoElement });
        },
        width: 1280,
        height: 720
    });
    camera.start();
}

// Hide spinner once the first frame arrives
const spinner = document.querySelector('.loading');
spinner.ontransitionend = () => { spinner.style.display = 'none'; };

function onResults(component, results) {
    document.body.classList.add('loaded');

    canvasCtx.save();
    canvasCtx.clearRect(0, 0, canvasElement.width, canvasElement.height);

    // selfieMode: true already mirrors both the image and the landmark coordinates
    canvasCtx.drawImage(results.image, 0, 0, canvasElement.width, canvasElement.height);

    if (results.multiFaceLandmarks) {
        for (const landmarks of results.multiFaceLandmarks) {
            // Full face tessellation mesh (semi-transparent grey)
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_TESSELATION,
                { color: '#C0C0C055', lineWidth: 1 });

            // Face oval
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_FACE_OVAL,
                { color: '#E0E0E0', lineWidth: 2 });

            // Right eye + eyebrow (red)
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_RIGHT_EYE,
                { color: '#FF3030', lineWidth: 2 });
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_RIGHT_EYEBROW,
                { color: '#FF3030', lineWidth: 2 });

            // Left eye + eyebrow (green)
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_LEFT_EYE,
                { color: '#30FF30', lineWidth: 2 });
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_LEFT_EYEBROW,
                { color: '#30FF30', lineWidth: 2 });

            // Iris (requires refineLandmarks: true)
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_RIGHT_IRIS,
                { color: '#FF3030', lineWidth: 2 });
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_LEFT_IRIS,
                { color: '#30FF30', lineWidth: 2 });

            // Lips (white)
            drawingUtils.drawConnectors(
                canvasCtx, landmarks, mpFaceMesh.FACEMESH_LIPS,
                { color: '#FFFFFF', lineWidth: 2 });
        }
    }

    // Send landmark data back to Blazor as JSON
    if (results.multiFaceLandmarks && results.multiFaceLandmarks.length > 0) {
        const faces = results.multiFaceLandmarks.map((landmarks, index) => ({
            index,
            landmarks   // array of {x, y, z} — already plain objects
        }));
        const json = JSON.stringify({ faces });
        detectFaceMeshExports.FaceMeshJsComponent.FaceMesh.Interop.OnResults(component, json);
    }
}
