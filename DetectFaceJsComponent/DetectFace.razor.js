// Face attendance detection via face-api.js
// Pattern mirrors DetectHands.razor.js — JSImport/JSExport bidirectional interop

let videoElement;
let canvasElement;
let canvasCtx;
let detectFaceExports;
let dotnetComponent = null;   // stored so module-scope functions can reach it
let currentDescriptor = null;
let componentMode = 'enroll';
let autoSendInterval = null;
let detectionRunning = false;
let detectionGeneration = 0;  // incremented on each onInit; old loops compare and exit
let lastFaceDetectedState = false;

function insertGlobalScript(url) {
    if (document.querySelector(`script[src="${url}"]`)) {
        return Promise.resolve();
    }
    const element = document.createElement('script');
    element.setAttribute('src', url);
    element.setAttribute('crossorigin', 'anonymous');
    document.head.appendChild(element);
    return new Promise((resolve, reject) => {
        element.onload = () => resolve();
        element.onerror = () => reject(new Error(`Failed to load: ${url}`));
    });
}

export async function onInit(component, mode) {
    // Tear down any previous session before starting a new one
    stopCurrentSession();

    dotnetComponent = component;
    videoElement = document.querySelector('.face_video');
    canvasElement = document.querySelector('.face_canvas');
    canvasCtx = canvasElement.getContext('2d');
    componentMode = mode;

    const { getAssemblyExports } = await globalThis.getDotnetRuntime(0);
    detectFaceExports = await getAssemblyExports("DetectFaceJsComponent.dll");

    // Load face-api.js from CDN
    await insertGlobalScript('https://cdn.jsdelivr.net/npm/face-api.js@0.22.2/dist/face-api.min.js');

    const MODEL_URL = 'https://cdn.jsdelivr.net/gh/justadudewhohacks/face-api.js@0.22.2/weights';

    updateLoadingMessage('Loading face detector...');
    await faceapi.nets.tinyFaceDetector.loadFromUri(MODEL_URL);

    updateLoadingMessage('Loading landmark model...');
    await faceapi.nets.faceLandmark68TinyNet.loadFromUri(MODEL_URL);

    updateLoadingMessage('Loading recognition model...');
    await faceapi.nets.faceRecognitionNet.loadFromUri(MODEL_URL);

    updateLoadingMessage('Starting camera...');
    await startCamera();

    // In check-in mode, auto-send descriptor every 1.5 s when a face is present
    if (componentMode === 'checkin') {
        autoSendInterval = setInterval(() => {
            if (currentDescriptor) {
                sendDescriptorToNet(component, currentDescriptor);
            }
        }, 1500);
    }

    document.body.classList.add('loaded');
    console.log('face-api.js ready, mode:', mode);
}

function updateLoadingMessage(msg) {
    const msgEl = document.querySelector('.face-container .message');
    if (msgEl) msgEl.textContent = msg;
}

async function startCamera() {
    const stream = await navigator.mediaDevices.getUserMedia({ video: true });
    videoElement.srcObject = stream;
    await videoElement.play();

    canvasElement.width = videoElement.videoWidth || 640;
    canvasElement.height = videoElement.videoHeight || 480;

    const spinner = document.querySelector('.face-container .loading');
    if (spinner) {
        spinner.ontransitionend = () => { spinner.style.display = 'none'; };
        spinner.style.opacity = '0';
    }

    detectionRunning = true;
    detectionGeneration++;                    // invalidates any still-running old loop
    runDetectionLoop(detectionGeneration);
}

async function runDetectionLoop(generation) {
    const options = new faceapi.TinyFaceDetectorOptions({ inputSize: 320, scoreThreshold: 0.5 });

    while (detectionRunning && generation === detectionGeneration) {
        if (!videoElement.paused && !videoElement.ended && videoElement.readyState >= 2) {
            try {
                const detection = await faceapi
                    .detectSingleFace(videoElement, options)
                    .withFaceLandmarks(true)
                    .withFaceDescriptor();

                // Exit immediately if this loop was superseded while awaiting
                if (generation !== detectionGeneration) break;

                currentDescriptor = detection ? detection.descriptor : null;
                drawOverlay(detection);

                // Notify C# only when detection state flips; guard against disposed component
                const nowDetected = detection != null;
                if (nowDetected !== lastFaceDetectedState && dotnetComponent) {
                    lastFaceDetectedState = nowDetected;
                    detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnFaceStatusChanged(dotnetComponent, nowDetected);
                }
            } catch (e) {
                console.warn('Detection error:', e);
            }
        }
        // Yield between frames — face-api.js is CPU-intensive
        await new Promise(resolve => setTimeout(resolve, 150));
    }
    console.log('Detection loop exited, generation:', generation);
}

function drawOverlay(detection) {
    canvasCtx.clearRect(0, 0, canvasElement.width, canvasElement.height);
    canvasCtx.drawImage(videoElement, 0, 0, canvasElement.width, canvasElement.height);

    if (detection) {
        const dims = { width: canvasElement.width, height: canvasElement.height };
        const resized = faceapi.resizeResults(detection, dims);
        faceapi.draw.drawDetections(canvasElement, [resized]);
        faceapi.draw.drawFaceLandmarks(canvasElement, [resized]);
    }
}

function sendDescriptorToNet(component, descriptor) {
    const json = JSON.stringify({
        detected: true,
        descriptor: Array.from(descriptor)
    });
    detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnDescriptorReady(component, json);
}

export async function captureDescriptor(component) {
    if (currentDescriptor) {
        sendDescriptorToNet(component, currentDescriptor);
    } else {
        const json = JSON.stringify({ detected: false, descriptor: null });
        detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnDescriptorReady(component, json);
    }
}

export function dispose() {
    stopCurrentSession();
}

function stopCurrentSession() {
    dotnetComponent = null;       // prevent callbacks to the (about-to-be) disposed component
    currentDescriptor = null;
    lastFaceDetectedState = false;
    detectionRunning = false;     // signals the loop to exit after its current await
    if (autoSendInterval) {
        clearInterval(autoSendInterval);
        autoSendInterval = null;
    }
    if (videoElement && videoElement.srcObject) {
        videoElement.srcObject.getTracks().forEach(t => t.stop());
        videoElement.srcObject = null;
    }
}
