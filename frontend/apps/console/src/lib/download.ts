/** Saves bytes as a file in the browser (a CSV the API returned). The bytes are kept as they are, so the UTF-8 byte-order mark reaches Excel. */
export function saveFile(fileName: string, bytes: ArrayBuffer, type = 'text/csv;charset=utf-8'): void {
  const url = URL.createObjectURL(new Blob([bytes], { type }));
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
