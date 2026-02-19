import React, { useCallback, useEffect, useMemo, useRef, useState } from "https://esm.sh/react@18";
import { createRoot } from "https://esm.sh/react-dom@18/client";

const LIST_ENDPOINT = "/api/files";
const CHUNK_UPLOAD_ENDPOINT = "/api/files/upload/chunk";
const COMPLETE_UPLOAD_ENDPOINT = "/api/files/upload/complete";
const CHUNK_SIZE_BYTES = 4 * 1024 * 1024;
const CHUNK_UPLOAD_RETRIES = 3;
const h = React.createElement;

function formatBytes(bytes) {
  if (bytes === 0) {
    return "0 B";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  const base = 1024;
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(base)), units.length - 1);
  const value = bytes / base ** exponent;
  return `${value.toFixed(value >= 10 || exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}

function formatUtc(isoOrDate) {
  const date = new Date(isoOrDate);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  return date.toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
    timeZone: "UTC",
    timeZoneName: "short"
  });
}

function encodeRelativePath(path) {
  return path
    .split("/")
    .filter(Boolean)
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

function createUploadId() {
  if (globalThis.crypto && typeof globalThis.crypto.randomUUID === "function") {
    return globalThis.crypto.randomUUID().replace(/[^a-zA-Z0-9_-]/g, "");
  }

  return `upload_${Date.now()}_${Math.random().toString(16).slice(2)}`;
}

function getDownloadPath(relativePath) {
  return `/api/files/download/${encodeRelativePath(relativePath)}`;
}

function buildPermalink(relativePath) {
  return new URL(getDownloadPath(relativePath), window.location.origin).toString();
}

function App() {
  const [files, setFiles] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [path, setPath] = useState("");
  const [selectedFile, setSelectedFile] = useState(null);
  const [uploadState, setUploadState] = useState({
    status: "idle",
    message: "",
    progress: 0
  });
  const [copiedLink, setCopiedLink] = useState("");
  const fileInputRef = useRef(null);

  const loadFiles = useCallback(async () => {
    setLoading(true);
    setError("");

    try {
      const response = await fetch(LIST_ENDPOINT);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const payload = await response.json();
      setFiles(Array.isArray(payload) ? payload : []);
    } catch (err) {
      setError(`Failed to load files: ${err.message}`);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadFiles();
  }, [loadFiles]);

  function handleFileChange(event) {
    const file = event.target.files && event.target.files[0] ? event.target.files[0] : null;
    setSelectedFile(file);
  }

  async function extractUploadErrorMessage(response) {
    let responseText = "";
    try {
      responseText = (await response.text()).trim();
    } catch {
      // Ignore body parsing failures and keep fallback message.
    }

    if (!responseText) {
      return `Upload failed with HTTP ${response.status}.`;
    }

    try {
      const parsed = JSON.parse(responseText);
      if (typeof parsed === "string") {
        return `Upload failed: ${parsed}`;
      }

      if (parsed && typeof parsed.detail === "string" && parsed.detail) {
        return `Upload failed: ${parsed.detail}`;
      }

      if (parsed && typeof parsed.title === "string") {
        return `Upload failed: ${parsed.title}`;
      }
    } catch {
      // Keep plain-text response as-is.
    }

    return `Upload failed: ${responseText}`;
  }

  async function handleUpload(event) {
    event.preventDefault();
    if (!selectedFile) {
      setUploadState({
        status: "error",
        message: "Choose a file before uploading.",
        progress: 0
      });
      return;
    }

    const trimmedPath = path.trim();
    const uploadId = createUploadId();
    const totalChunks = Math.max(1, Math.ceil(selectedFile.size / CHUNK_SIZE_BYTES));

    setUploadState({
      status: "uploading",
      message: "Uploading...",
      progress: 0
    });

    try {
      for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex += 1) {
        const chunkStart = chunkIndex * CHUNK_SIZE_BYTES;
        const chunkEnd = Math.min(chunkStart + CHUNK_SIZE_BYTES, selectedFile.size);
        const chunkBlob = selectedFile.slice(chunkStart, chunkEnd);

        const formData = new FormData();
        formData.append("chunk", chunkBlob, `${selectedFile.name}.part`);
        formData.append("uploadId", uploadId);
        formData.append("fileName", selectedFile.name);
        formData.append("chunkIndex", String(chunkIndex));
        formData.append("totalChunks", String(totalChunks));
        if (trimmedPath) {
          formData.append("path", trimmedPath);
        }

        let chunkUploaded = false;
        let lastChunkError = null;

        for (let attempt = 1; attempt <= CHUNK_UPLOAD_RETRIES; attempt += 1) {
          try {
            const chunkResponse = await fetch(CHUNK_UPLOAD_ENDPOINT, {
              method: "POST",
              body: formData
            });

            if (!chunkResponse.ok) {
              throw new Error(await extractUploadErrorMessage(chunkResponse));
            }

            chunkUploaded = true;
            break;
          } catch (error) {
            lastChunkError = error;
            if (attempt < CHUNK_UPLOAD_RETRIES) {
              await new Promise((resolve) => setTimeout(resolve, attempt * 600));
            }
          }
        }

        if (!chunkUploaded) {
          throw (lastChunkError instanceof Error
            ? lastChunkError
            : new Error("Upload failed due to a network error."));
        }

        const bytesUploaded = chunkEnd;
        const progress = selectedFile.size > 0
          ? Math.min(100, Math.round((bytesUploaded / selectedFile.size) * 100))
          : Math.round(((chunkIndex + 1) / totalChunks) * 100);
        setUploadState({
          status: "uploading",
          message: `Uploading... ${progress}%`,
          progress
        });
      }

      const completeResponse = await fetch(COMPLETE_UPLOAD_ENDPOINT, {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          uploadId,
          fileName: selectedFile.name,
          path: trimmedPath || "",
          totalChunks
        })
      });

      if (!completeResponse.ok) {
        throw new Error(await extractUploadErrorMessage(completeResponse));
      }

      let relativePath = selectedFile.name;
      try {
        const payload = await completeResponse.json();
        if (payload && typeof payload.relativePath === "string" && payload.relativePath) {
          relativePath = payload.relativePath;
        }
      } catch {
        // Ignore JSON parsing errors and keep fallback display name.
      }

      setUploadState({
        status: "success",
        message: `Uploaded ${relativePath}.`,
        progress: 100
      });
      setSelectedFile(null);
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
      await loadFiles();
    } catch (err) {
      setUploadState({
        status: "error",
        message: err instanceof Error ? err.message : "Upload failed due to a network error.",
        progress: 0
      });
    }
  }

  async function handleCopyPermalink(relativePath) {
    const permalink = buildPermalink(relativePath);

    try {
      if (!navigator.clipboard || typeof navigator.clipboard.writeText !== "function") {
        throw new Error("Clipboard API unavailable");
      }

      await navigator.clipboard.writeText(permalink);
      setCopiedLink(relativePath);
      setTimeout(() => {
        setCopiedLink((current) => (current === relativePath ? "" : current));
      }, 1800);
    } catch {
      window.prompt("Copy permalink:", permalink);
    }
  }

  const totalSize = useMemo(() => files.reduce((sum, file) => sum + (file.size || 0), 0), [files]);
  const isUploading = uploadState.status === "uploading";

  return h(
    "main",
    { className: "page" },
    h("h1", null, "Fileshare"),
    h(
      "p",
      { className: "subtitle" },
      loading ? "Loading..." : `${files.length} files, ${formatBytes(totalSize)} total`
    ),
    h(
      "form",
      { className: "upload-card", onSubmit: handleUpload },
      h("h2", null, "Upload File"),
      h(
        "div",
        { className: "upload-controls" },
        h(
          "label",
          { className: "field" },
          h("span", null, "File"),
          h("input", {
            type: "file",
            name: "file",
            ref: fileInputRef,
            onChange: handleFileChange,
            disabled: isUploading
          })
        ),
        h(
          "label",
          { className: "field" },
          h("span", null, "Subfolder (optional)"),
          h("input", {
            type: "text",
            name: "path",
            value: path,
            onChange: (event) => setPath(event.target.value),
            placeholder: "reports/2026",
            disabled: isUploading
          })
        ),
        h(
          "button",
          { type: "submit", disabled: isUploading || !selectedFile },
          isUploading ? "Uploading..." : "Upload"
        )
      ),
      uploadState.status === "idle"
        ? null
        : h(
            "p",
            { className: `upload-status ${uploadState.status}` },
            uploadState.message
          )
    ),
    error ? h("p", { className: "error" }, error) : null,
    h(
      "div",
      { className: "table-wrap" },
      h(
        "table",
        null,
        h(
          "thead",
          null,
          h(
            "tr",
            null,
            h("th", null, "Name"),
            h("th", null, "Size"),
            h("th", null, "Modified (UTC)"),
            h("th", null, "Actions")
          )
        ),
        h(
          "tbody",
          null,
          files.map((file) =>
            h(
              "tr",
              { key: file.relativePath },
              h("td", { className: "name-cell", title: file.relativePath }, file.relativePath),
              h("td", null, formatBytes(file.size || 0)),
              h("td", null, formatUtc(file.lastModifiedUtc)),
              h(
                "td",
                { className: "actions-cell" },
                h(
                  "a",
                  { href: getDownloadPath(file.relativePath) },
                  "Download"
                ),
                h(
                  "button",
                  {
                    type: "button",
                    onClick: () => handleCopyPermalink(file.relativePath)
                  },
                  copiedLink === file.relativePath ? "Copied!" : "Permalink"
                )
              )
            )
          )
        )
      )
    )
  );
}

createRoot(document.getElementById("root")).render(h(App));
