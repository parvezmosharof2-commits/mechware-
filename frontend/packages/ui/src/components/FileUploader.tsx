'use client';

import React, { useState, useRef } from 'react';
import { UploadCloud, FileText, CheckCircle2, X, AlertCircle } from 'lucide-react';
import { cn } from '../utils';
import { Progress } from './Progress';

export interface UploadedFileItem {
  id: string;
  name: string;
  size: number;
  type: string;
  progress: number;
  status: 'uploading' | 'completed' | 'error';
  errorMessage?: string;
}

export interface FileUploaderProps {
  label?: string;
  helperText?: string;
  accept?: string;
  maxSizeMb?: number;
  multiple?: boolean;
  onFilesSelected?: (files: File[]) => void;
  className?: string;
}

export const FileUploader: React.FC<FileUploaderProps> = ({
  label = 'Upload Industrial CAD / Compliance Document',
  helperText = 'Supported formats: PDF, STEP, DWG, PNG (Max 25MB)',
  accept = '.pdf,.step,.dwg,.png,.jpg',
  maxSizeMb = 25,
  multiple = true,
  onFilesSelected,
  className,
}) => {
  const [isDragging, setIsDragging] = useState(false);
  const [files, setFiles] = useState<UploadedFileItem[]>([]);
  const inputRef = useRef<HTMLInputElement>(null);

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const processFiles = (rawFiles: FileList | null) => {
    if (!rawFiles || rawFiles.length === 0) return;

    const fileArr = Array.from(rawFiles);
    onFilesSelected?.(fileArr);

    const newItems: UploadedFileItem[] = fileArr.map((f) => {
      const isOverSize = f.size > maxSizeMb * 1024 * 1024;
      return {
        id: Math.random().toString(36).substring(2, 9),
        name: f.name,
        size: f.size,
        type: f.type,
        progress: isOverSize ? 0 : 100,
        status: isOverSize ? 'error' : 'completed',
        errorMessage: isOverSize ? `File exceeds maximum ${maxSizeMb}MB limit` : undefined,
      };
    });

    setFiles((prev) => (multiple ? [...prev, ...newItems] : newItems));
  };

  const removeFile = (id: string) => {
    setFiles((prev) => prev.filter((f) => f.id !== id));
  };

  return (
    <div className={cn('w-full flex flex-col gap-2 font-sans', className)}>
      {label && <label className="text-sm font-medium text-text">{label}</label>}

      {/* Drag & Drop Area */}
      <div
        onDragOver={(e) => {
          e.preventDefault();
          setIsDragging(true);
        }}
        onDragLeave={() => setIsDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setIsDragging(false);
          processFiles(e.dataTransfer.files);
        }}
        onClick={() => inputRef.current?.click()}
        className={cn(
          'relative flex flex-col items-center justify-center p-6 border-2 border-dashed rounded-xl transition-all cursor-pointer bg-bg-muted/40 hover:bg-bg-muted/70',
          isDragging
            ? 'border-accent bg-accent/10 shadow-glow-cyan'
            : 'border-surface-border-strong hover:border-accent'
        )}
      >
        <input
          ref={inputRef}
          type="file"
          accept={accept}
          multiple={multiple}
          aria-label={label || 'File Upload'}
          className="hidden"
          onChange={(e) => processFiles(e.target.files)}
        />

        <div className="flex h-12 w-12 items-center justify-center rounded-full bg-accent/10 text-accent mb-3">
          <UploadCloud className="h-6 w-6" />
        </div>

        <p className="text-sm font-medium text-text text-center">
          <span className="text-accent underline font-semibold">Click to upload</span> or drag and drop
        </p>
        <p className="text-xs text-text-muted mt-1 text-center">{helperText}</p>
      </div>

      {/* File List */}
      {files.length > 0 && (
        <div className="flex flex-col gap-2 mt-2">
          {files.map((file) => (
            <div
              key={file.id}
              className="flex items-center justify-between rounded-md border border-surface-border bg-surface p-3 text-sm shadow-sm"
            >
              <div className="flex items-center gap-3 overflow-hidden">
                <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-bg-muted text-text-muted">
                  <FileText className="h-5 w-5" />
                </div>
                <div className="flex flex-col min-w-0">
                  <span className="font-medium text-text truncate max-w-xs">{file.name}</span>
                  <div className="flex items-center gap-2 text-xs text-text-subtle">
                    <span>{formatFileSize(file.size)}</span>
                    {file.status === 'completed' && (
                      <span className="flex items-center gap-1 text-status-online">
                        <CheckCircle2 className="h-3 w-3" /> Ready
                      </span>
                    )}
                    {file.status === 'error' && (
                      <span className="flex items-center gap-1 text-fault font-medium">
                        <AlertCircle className="h-3 w-3" /> {file.errorMessage}
                      </span>
                    )}
                  </div>
                </div>
              </div>

              <div className="flex items-center gap-3">
                {file.status === 'uploading' && (
                  <div className="w-24">
                    <Progress value={file.progress} variant="accent" />
                  </div>
                )}
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    removeFile(file.id);
                  }}
                  className="text-text-subtle hover:text-text p-1 rounded-sm transition-colors"
                  aria-label={`Remove ${file.name}`}
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
