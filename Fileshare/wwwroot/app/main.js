import React, { useCallback, useEffect, useMemo, useRef, useState } from "https://esm.sh/react@18";
import { createRoot } from "https://esm.sh/react-dom@18/client";

const LIST_ENDPOINT = "/api/files";
const UPLOAD_ENDPOINT = "/api/files/upload";
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

  function extractUploadErrorMessage(xhr) {
    const responseText = typeof xhr.responseText === "string" ? xhr.responseText.trim() : "";
    if (!responseText) {
      return `Upload failed with HTTP ${xhr.status}.`;
    }

    try {
      const parsed = JSON.parse(responseText);
      if (typeof parsed === "string") {
        return `Upload failed: ${parsed}`;
      }

      if (parsed && typeof parsed.title === "string") {
        return `Upload failed: ${parsed.title}`;
      }
    } catch {
      // Keep plain-text response as-is.
    }

    return `Upload failed: ${responseText}`;
  }

  function handleUpload(event) {
    event.preventDefault();
    if (!selectedFile) {
      setUploadState({
        status: "error",
        message: "Choose a file before uploading.",
        progress: 0
      });
      return;
    }

    const formData = new FormData();
    formData.append("file", selectedFile);

    const trimmedPath = path.trim();
    if (trimmedPath) {
      formData.append("path", trimmedPath);
    }

    setUploadState({
      status: "uploading",
      message: "Uploading...",
      progress: 0
    });

    const xhr = new XMLHttpRequest();
    xhr.open("POST", UPLOAD_ENDPOINT);

    xhr.upload.onprogress = (uploadEvent) => {
      if (!uploadEvent.lengthComputable) {
        return;
      }

      const progress = Math.min(100, Math.round((uploadEvent.loaded / uploadEvent.total) * 100));
      setUploadState({
        status: "uploading",
        message: `Uploading... ${progress}%`,
        progress
      });
    };

    xhr.onload = async () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        let relativePath = selectedFile.name;
        try {
          const payload = JSON.parse(xhr.responseText);
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
        return;
      }

      setUploadState({
        status: "error",
        message: extractUploadErrorMessage(xhr),
        progress: 0
      });
    };

    xhr.onerror = () => {
      setUploadState({
        status: "error",
        message: "Upload failed due to a network error.",
        progress: 0
      });
    };

    xhr.send(formData);
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
            h("th", null, "Download")
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
                null,
                h(
                  "a",
                  { href: `/api/files/download/${encodeRelativePath(file.relativePath)}` },
                  "Download"
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
