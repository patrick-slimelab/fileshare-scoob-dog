import React, { useEffect, useMemo, useState } from "https://esm.sh/react@18";
import { createRoot } from "https://esm.sh/react-dom@18/client";

const LIST_ENDPOINT = "/api/files";

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

  useEffect(() => {
    async function loadFiles() {
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
    }

    loadFiles();
  }, []);

  const totalSize = useMemo(() => files.reduce((sum, file) => sum + (file.size || 0), 0), [files]);

  return React.createElement(
    "main",
    { className: "page" },
    React.createElement("h1", null, "Fileshare"),
    React.createElement(
      "p",
      { className: "subtitle" },
      loading ? "Loading..." : `${files.length} files, ${formatBytes(totalSize)} total`
    ),
    error ? React.createElement("p", { className: "error" }, error) : null,
    React.createElement(
      "div",
      { className: "table-wrap" },
      React.createElement(
        "table",
        null,
        React.createElement(
          "thead",
          null,
          React.createElement(
            "tr",
            null,
            React.createElement("th", null, "Name"),
            React.createElement("th", null, "Size"),
            React.createElement("th", null, "Modified (UTC)"),
            React.createElement("th", null, "Download")
          )
        ),
        React.createElement(
          "tbody",
          null,
          files.map((file) =>
            React.createElement(
              "tr",
              { key: file.relativePath },
              React.createElement("td", { className: "name-cell", title: file.relativePath }, file.relativePath),
              React.createElement("td", null, formatBytes(file.size || 0)),
              React.createElement("td", null, formatUtc(file.lastModifiedUtc)),
              React.createElement(
                "td",
                null,
                React.createElement(
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

createRoot(document.getElementById("root")).render(React.createElement(App));
