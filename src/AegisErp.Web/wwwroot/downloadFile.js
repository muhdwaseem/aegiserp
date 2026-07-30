// Triggers a browser download of an in-memory byte array, since Blazor Server has no direct
// filesystem access on the client — the bytes arrive here as a base64 string over the circuit.
window.downloadFileFromBytes = (fileName, contentType, base64Data) => {
    const bytes = atob(base64Data);
    const buffer = new Uint8Array(bytes.length);
    for (let i = 0; i < bytes.length; i++) buffer[i] = bytes.charCodeAt(i);

    const blob = new Blob([buffer], { type: contentType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};
