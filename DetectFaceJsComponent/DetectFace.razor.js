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
let lastRecognizedName = '';  // drawn on canvas in check-in mode

// ── Liveness state ───────────────────────────────────────────────────────────
let livenessMode = 'none';           // 'none' | 'blink' | 'minifasnet'
let livenessConfirmed = false;
let livenessModelLoading = false;
let livenessOrtSession = null;       // ONNX InferenceSession (MiniFASNet)
let blinkState = 'open';             // 'open' | 'was_closed' | 'sustained'
let wasClosedFrameCount = 0;         // frames spent in was_closed state
let lastFaceBox = null;              // previous frame face box — used for movement detection
let stableFrameCount = 0;            // consecutive frames with low face movement
const EAR_BLINK_THRESHOLD = 0.29;   // tuned: open-eye ~0.30-0.32, blink dip ~0.282-0.286
const EAR_OPEN_THRESHOLD  = 0.30;   // hysteresis: EAR must recover above this to exit 'sustained'
const MAX_CLOSED_FRAMES   = 4;      // EAR below threshold >4 frames = sustained gaze, not a blink
const FACE_MOVE_THRESHOLD = 0.03;   // max frame-to-frame face-center shift (fraction of face width)
const MIN_STABLE_FRAMES   = 4;      // face must be stable for this many frames before blink accepted

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

export async function onInit(component, mode, liveness) {
    // Tear down any previous session before starting a new one
    stopCurrentSession();

    dotnetComponent = component;
    videoElement = document.querySelector('.face_video');
    canvasElement = document.querySelector('.face_canvas');
    canvasCtx = canvasElement.getContext('2d');
    componentMode = mode;
    livenessMode  = liveness ?? 'none';

    const { getAssemblyExports } = await globalThis.getDotnetRuntime(0);
    detectFaceExports = await getAssemblyExports("DetectFaceJsComponent.dll");

    // Load face-api.js from CDN
    await insertGlobalScript('https://cdn.jsdelivr.net/npm/face-api.js@0.22.2/dist/face-api.min.js');

    const MODEL_URL = 'https://cdn.jsdelivr.net/gh/justadudewhohacks/face-api.js@0.22.2/weights';

    updateLoadingMessage('Loading face detector...');
    await faceapi.nets.ssdMobilenetv1.loadFromUri(MODEL_URL);

    updateLoadingMessage('Loading landmark model...');
    await faceapi.nets.faceLandmark68Net.loadFromUri(MODEL_URL);

    updateLoadingMessage('Loading recognition model...');
    await faceapi.nets.faceRecognitionNet.loadFromUri(MODEL_URL);

    updateLoadingMessage('Starting camera...');
    await startCamera();

    // In check-in mode, auto-send descriptor every 1.5 s when liveness passes.
    // Liveness is NOT reset here — C# resets it via ResetLiveness() after a
    // successful check-in, avoiding a race where the async HandleCheckin runs
    // after the synchronous JS reset and sees LivenessConfirmed = false.
    if (componentMode === 'checkin') {
        autoSendInterval = setInterval(() => {
            if (currentDescriptor && (livenessMode === 'none' || livenessConfirmed)) {
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
    let stream;
    try {
        stream = await navigator.mediaDevices.getUserMedia({ video: true });
    } catch (err) {
        const msg = err.name === 'NotAllowedError'
            ? 'Failed to acquire camera feed: NotAllowedError: Permission denied'
            : `Failed to acquire camera feed: ${err.message}`;
        updateLoadingMessage(`⚠️ ${msg}`);
        window.alert(msg);
        throw err;  // re-throw so onInit propagates the failure to C#
    }
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
    const options = new faceapi.SsdMobilenetv1Options({ minConfidence: 0.5 });

    while (detectionRunning && generation === detectionGeneration) {
        if (!videoElement.paused && !videoElement.ended && videoElement.readyState >= 2) {
            try {
                const detection = await faceapi
                    .detectSingleFace(videoElement, options)
                    .withFaceLandmarks()         // full 68-point model (paired with SsdMobilenetv1)
                    .withFaceDescriptor();

                // Exit immediately if this loop was superseded while awaiting
                if (generation !== detectionGeneration) break;

                currentDescriptor = detection ? detection.descriptor : null;
                drawOverlay(detection);

                // ── Liveness check ──────────────────────────────────────────
                if (livenessMode !== 'none') {
                    const wasConfirmed = livenessConfirmed;
                    if (detection) {
                        if (livenessMode === 'blink') {
                            checkLivenessEAR(detection);
                        } else if (livenessMode === 'minifasnet') {
                            await checkLivenessOnnx(detection);
                            if (generation !== detectionGeneration) break;
                        }
                    } else if (livenessMode === 'blink') {
                        // Face lost — full reset
                        livenessConfirmed = false;
                        blinkState = 'open';
                        wasClosedFrameCount = 0;
                        lastFaceBox = null;
                        stableFrameCount = 0;
                    }
                    if (livenessConfirmed !== wasConfirmed && dotnetComponent) {
                        detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnLivenessChanged(dotnetComponent, livenessConfirmed);
                    }
                }

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
        // Blink mode needs faster sampling to catch the brief eye-closed moment;
        // other modes can stay at the slower rate to keep CPU usage down.
        const frameDelay = livenessMode === 'blink' ? 30 : 300;
        await new Promise(resolve => setTimeout(resolve, frameDelay));
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

        // Draw recognised name badge below the bounding box (check-in mode only)
        // The confidence score is already drawn top-left by drawDetections above.
        if (componentMode === 'checkin' && lastRecognizedName) {
            const box = resized.detection.box;
            const isKnown = lastRecognizedName !== '?';
            const label = lastRecognizedName;

            canvasCtx.save();
            canvasCtx.font = 'bold 18px sans-serif';
            const textW = canvasCtx.measureText(label).width;
            const padX = 8, padY = 4, boxH = 26;
            const bx = box.x;
            const by = Math.min(box.y + box.height + padY, canvasElement.height - boxH);

            // Coloured background pill
            canvasCtx.fillStyle = isKnown ? 'rgba(0,180,0,0.82)' : 'rgba(200,50,50,0.82)';
            canvasCtx.beginPath();
            canvasCtx.roundRect(bx, by, textW + padX * 2, boxH, 5);
            canvasCtx.fill();

            // Name text
            canvasCtx.fillStyle = 'white';
            canvasCtx.fillText(label, bx + padX, by + boxH - 6);
            canvasCtx.restore();
        }
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
    const SAMPLE_COUNT = 5;
    const SAMPLE_DELAY_MS = 200;
    const options = new faceapi.SsdMobilenetv1Options({ minConfidence: 0.5 });
    const descriptors = [];

    for (let i = 0; i < SAMPLE_COUNT; i++) {
        if (videoElement && videoElement.readyState >= 2) {
            try {
                const det = await faceapi
                    .detectSingleFace(videoElement, options)
                    .withFaceLandmarks()
                    .withFaceDescriptor();
                if (det) descriptors.push(det.descriptor);
            } catch (e) {
                console.warn('Capture sample error:', e);
            }
        }
        if (i < SAMPLE_COUNT - 1) {
            await new Promise(resolve => setTimeout(resolve, SAMPLE_DELAY_MS));
        }
    }

    if (descriptors.length === 0) {
        const json = JSON.stringify({ detected: false, descriptor: null, sampleCount: 0 });
        detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnDescriptorReady(component, json);
        return;
    }

    // Element-wise average across all captured samples
    const avg = new Float32Array(128);
    for (const desc of descriptors) {
        for (let i = 0; i < 128; i++) avg[i] += desc[i];
    }
    for (let i = 0; i < 128; i++) avg[i] /= descriptors.length;

    const json = JSON.stringify({
        detected: true,
        descriptor: Array.from(avg),
        sampleCount: descriptors.length
    });
    detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnDescriptorReady(component, json);
}

export function setRecognizedName(name) {
    lastRecognizedName = name ?? '';
}

export function setLivenessMode(mode) {
    livenessMode = mode ?? 'none';
    livenessConfirmed = false;
    blinkState = 'open';
    wasClosedFrameCount = 0;
    lastFaceBox = null;
    stableFrameCount = 0;
    if (mode === 'minifasnet') ensureOnnxSession();
    if (dotnetComponent) {
        detectFaceExports.DetectFaceJsComponent.DetectFace.Interop.OnLivenessChanged(dotnetComponent, false);
    }
}

export function resetLiveness() {
    livenessConfirmed = false;
    blinkState = 'open';
    wasClosedFrameCount = 0;
    lastFaceBox = null;
    stableFrameCount = 0;
}

export function dispose() {
    stopCurrentSession();
}

// ── Liveness: EAR Blink Detection ────────────────────────────────────────────
// Uses face-api.js built-in getLeftEye() / getRightEye() (6 pts each) instead of
// raw position indices so the code is robust across all face-api.js build variants.
//
// Eye point layout (same for left and right eye):
//   [0] outer corner  [1] upper-outer  [2] upper-inner
//   [3] inner corner  [4] lower-inner  [5] lower-outer
//
// EAR = ( ||[1]-[5]|| + ||[2]-[4]|| ) / ( 2 * ||[0]-[3]|| )

function d2(a, b) { return Math.hypot(a.x - b.x, a.y - b.y); }

function eyeEAR(pts) {
    return (d2(pts[1], pts[5]) + d2(pts[2], pts[4]))
         / (2 * d2(pts[0], pts[3]));
}

function checkLivenessEAR(detection) {
    // ── Face stability gate (anti photo-on-phone attack) ──────────────────────
    const box = detection.detection.box;
    if (lastFaceBox) {
        const dx = (box.x + box.width  / 2) - (lastFaceBox.x + lastFaceBox.width  / 2);
        const dy = (box.y + box.height / 2) - (lastFaceBox.y + lastFaceBox.height / 2);
        const movement = Math.hypot(dx, dy) / box.width;
        if (movement > FACE_MOVE_THRESHOLD) {
            stableFrameCount = 0;
            wasClosedFrameCount = 0;
            blinkState = 'open';
            lastFaceBox = box;
            return;
        }
    }
    lastFaceBox = box;
    stableFrameCount = Math.min(stableFrameCount + 1, MIN_STABLE_FRAMES);

    // Require N consecutive stable frames before any blink can register
    if (stableFrameCount < MIN_STABLE_FRAMES) return;

    // ── EAR blink state machine (3 states) ─────────────────────────────────
    const lm  = detection.landmarks;
    const ear = (eyeEAR(lm.getLeftEye()) + eyeEAR(lm.getRightEye())) / 2;

    if (blinkState === 'open') {
        if (ear < EAR_BLINK_THRESHOLD) {
            blinkState = 'was_closed';
            wasClosedFrameCount = 1;
        }
    } else if (blinkState === 'was_closed') {
        if (ear >= EAR_BLINK_THRESHOLD) {
            blinkState = 'open';
            wasClosedFrameCount = 0;
            livenessConfirmed = true;
        } else {
            wasClosedFrameCount++;
            if (wasClosedFrameCount > MAX_CLOSED_FRAMES) {
                // EAR stayed low too long — sustained gaze, not a blink
                blinkState = 'sustained';
                wasClosedFrameCount = 0;
            }
        }
    } else if (blinkState === 'sustained') {
        if (ear >= EAR_OPEN_THRESHOLD) {
            blinkState = 'open';
        }
    }
}

// ── Liveness: MiniFASNet ONNX ─────────────────────────────────────────────────
// 1. Download an ONNX export of Silent-Face-Anti-Spoofing (minivision-ai).
// 2. Place the file at: AttendanceDemo/wwwroot/models/minifasnet.onnx
//
// Expected model contract:
//   Input  name : "input"  shape [1, 3, 80, 80]  — BGR, ImageNet normalised
//   Output shape: [1, 3] logits                  — class index 1 = live face
//
// A pre-converted model can be found at:
//   https://github.com/minivision-ai/Silent-Face-Anti-Spoofing (convert .pth → .onnx)

const FASNET_SIZE = 80;
const FASNET_MEAN = [0.485, 0.456, 0.406];
const FASNET_STD  = [0.229, 0.224, 0.225];

async function ensureOnnxSession() {
    if (livenessOrtSession || livenessModelLoading) return;
    livenessModelLoading = true;
    await insertGlobalScript('https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.1/dist/ort.min.js');
    const modelUrl = new URL('models/minifasnet.onnx', location.href).href;
    try {
        livenessOrtSession = await ort.InferenceSession.create(modelUrl, { executionProviders: ['wasm'] });
        console.log('[MiniFASNet] loaded from', modelUrl);
    } catch (e) {
        console.warn('[MiniFASNet] model not found at', modelUrl);
        console.warn('[MiniFASNet] Place minifasnet.onnx at AttendanceDemo/wwwroot/models/');
    }
    livenessModelLoading = false;
}

async function checkLivenessOnnx(detection) {
    await ensureOnnxSession();
    if (!livenessOrtSession) return;

    const box = detection.detection.box;
    const margin = box.width * 0.3;
    const sx = Math.max(0, box.x - margin);
    const sy = Math.max(0, box.y - margin);
    const sw = Math.min(videoElement.videoWidth  - sx, box.width  + margin * 2);
    const sh = Math.min(videoElement.videoHeight - sy, box.height + margin * 2);

    const offscreen = new OffscreenCanvas(FASNET_SIZE, FASNET_SIZE);
    const ctx = offscreen.getContext('2d');
    ctx.drawImage(videoElement, sx, sy, sw, sh, 0, 0, FASNET_SIZE, FASNET_SIZE);
    const px = ctx.getImageData(0, 0, FASNET_SIZE, FASNET_SIZE).data;

    // [1, 3, H, W] tensor — BGR channel order, ImageNet normalised
    const n = FASNET_SIZE * FASNET_SIZE;
    const tensor = new Float32Array(3 * n);
    for (let i = 0; i < n; i++) {
        tensor[0 * n + i] = (px[i*4+2] / 255 - FASNET_MEAN[2]) / FASNET_STD[2]; // B
        tensor[1 * n + i] = (px[i*4+1] / 255 - FASNET_MEAN[1]) / FASNET_STD[1]; // G
        tensor[2 * n + i] = (px[i*4+0] / 255 - FASNET_MEAN[0]) / FASNET_STD[0]; // R
    }

    try {
        const inputTensor = new ort.Tensor('float32', tensor, [1, 3, FASNET_SIZE, FASNET_SIZE]);
        const result = await livenessOrtSession.run({ input: inputTensor });
        const logits  = Array.from(result[Object.keys(result)[0]].data);
        const maxL    = Math.max(...logits);
        const exps    = logits.map(v => Math.exp(v - maxL));
        const liveProb = exps[1] / exps.reduce((a, b) => a + b, 0);
        livenessConfirmed = liveProb > 0.6;
    } catch (e) {
        console.warn('[MiniFASNet] inference error:', e.message);
    }
}

function stopCurrentSession() {
    dotnetComponent = null;       // prevent callbacks to the (about-to-be) disposed component
    currentDescriptor = null;
    lastFaceDetectedState = false;
    lastRecognizedName    = '';
    livenessConfirmed     = false;
    blinkState            = 'open';
    wasClosedFrameCount   = 0;
    lastFaceBox           = null;
    stableFrameCount      = 0;
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
