// ── Helpers ───────────────────────────────────────────────────────────────────

/** Convert a base64url string to an ArrayBuffer */
function base64urlToBuffer(base64url) {
    const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=');
    const binary = atob(padded);
    const buffer = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) buffer[i] = binary.charCodeAt(i);
    return buffer.buffer;
}

/** Convert an ArrayBuffer (or TypedArray) to a base64url string */
function bufferToBase64url(buffer) {
    const bytes = buffer instanceof ArrayBuffer ? new Uint8Array(buffer) : new Uint8Array(buffer.buffer);
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
}

// ── Public API ────────────────────────────────────────────────────────────────

/** Returns true when the browser supports the WebAuthn API */
export function isWebAuthnSupported() {
    return !!(window.PublicKeyCredential &&
              navigator.credentials &&
              typeof navigator.credentials.create === 'function');
}

/**
 * Run the WebAuthn registration ceremony in the browser.
 * @param {string} optionsJson  – JSON from the server (CredentialCreateOptions)
 * @returns {string}            – JSON to send back to the server (AttestationResponse)
 */
export async function startRegistration(optionsJson) {
    const options = JSON.parse(optionsJson);

    // Convert base64url fields → ArrayBuffer (required by WebAuthn API)
    options.challenge = base64urlToBuffer(options.challenge);
    options.user.id   = base64urlToBuffer(options.user.id);

    if (options.excludeCredentials?.length) {
        options.excludeCredentials = options.excludeCredentials.map(c => ({
            ...c,
            id: base64urlToBuffer(c.id)
        }));
    }

    let credential;
    try {
        credential = await navigator.credentials.create({ publicKey: options });
    } catch (err) {
        throw new Error(`WebAuthn registration failed: ${err.message}`);
    }

    // Serialize the authenticator response for the server
    return JSON.stringify({
        id:     credential.id,
        rawId:  bufferToBase64url(credential.rawId),
        type:   credential.type,
        response: {
            attestationObject: bufferToBase64url(credential.response.attestationObject),
            clientDataJSON:    bufferToBase64url(credential.response.clientDataJSON)
        }
    });
}

/**
 * Run the WebAuthn authentication ceremony in the browser.
 * @param {string} optionsJson  – JSON from the server (AssertionOptions)
 * @returns {string}            – JSON to send back to the server (AssertionResponse)
 */
export async function startAuthentication(optionsJson) {
    const options = JSON.parse(optionsJson);

    // Convert base64url fields → ArrayBuffer
    options.challenge = base64urlToBuffer(options.challenge);

    if (options.allowCredentials?.length) {
        options.allowCredentials = options.allowCredentials.map(c => ({
            ...c,
            id: base64urlToBuffer(c.id)
        }));
    }

    let credential;
    try {
        credential = await navigator.credentials.get({ publicKey: options });
    } catch (err) {
        throw new Error(`WebAuthn authentication failed: ${err.message}`);
    }

    return JSON.stringify({
        id:     credential.id,
        rawId:  bufferToBase64url(credential.rawId),
        type:   credential.type,
        response: {
            authenticatorData: bufferToBase64url(credential.response.authenticatorData),
            clientDataJSON:    bufferToBase64url(credential.response.clientDataJSON),
            signature:         bufferToBase64url(credential.response.signature),
            userHandle:        credential.response.userHandle
                                   ? bufferToBase64url(credential.response.userHandle)
                                   : null
        }
    });
}
