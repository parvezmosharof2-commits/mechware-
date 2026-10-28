'use client';

import React from 'react';
import { AlertCircle } from 'lucide-react';
import { cn } from '../utils';

export interface TextareaProps extends React.TextareaHTMLAttributes<HTMLTextAreaElement> {
  label?: string;
  helperText?: string;
  error?: string;
  maxLength?: number;
  showCharCount?: boolean;
}

export const Textarea = React.forwardRef<HTMLTextAreaElement, TextareaProps>(
  (
    {
      className,
      id,
      label,
      helperText,
      error,
      maxLength,
      showCharCount,
      disabled,
      value,
      defaultValue,
      onChange,
      rows = 4,
      ...props
    },
    ref
  ) => {
    const generatedId = React.useId();
    const textareaId = id || generatedId;
    const errorId = `${textareaId}-error`;
    const helperId = `${textareaId}-helper`;

    const [currentLength, setCurrentLength] = React.useState<number>(() => {
      if (typeof value === 'string') return value.length;
      if (typeof defaultValue === 'string') return defaultValue.length;
      return 0;
    });

    const handleChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
      setCurrentLength(e.target.value.length);
      onChange?.(e);
    };

    return (
      <div className="w-full flex flex-col gap-1.5 font-sans">
        {label && (
          <div className="flex justify-between items-center text-sm font-medium text-text">
            <label htmlFor={textareaId} className="cursor-pointer">
              {label}
              {props.required && <span className="text-fault ml-1">*</span>}
            </label>
            {showCharCount && maxLength && (
              <span className="text-xs text-text-subtle tabular-nums">
                {currentLength}/{maxLength}
              </span>
            )}
          </div>
        )}

        <div
          className={cn(
            'relative flex w-full rounded-md border bg-bg-muted transition-colors duration-fast overflow-hidden',
            error
              ? 'border-fault focus-within:ring-2 focus-within:ring-fault'
              : 'border-surface-border-strong focus-within:border-accent focus-within:ring-2 focus-within:ring-accent focus-within:ring-offset-2 focus-within:ring-offset-bg',
            disabled && 'opacity-50 cursor-not-allowed bg-bg-subtle',
            className
          )}
        >
          <textarea
            ref={ref}
            id={textareaId}
            rows={rows}
            disabled={disabled}
            value={value}
            defaultValue={defaultValue}
            maxLength={maxLength}
            onChange={handleChange}
            aria-invalid={Boolean(error)}
            aria-describedby={
              error ? errorId : helperText ? helperId : undefined
            }
            className="w-full bg-transparent px-3.5 py-2.5 text-text text-sm placeholder:text-text-subtle outline-none border-none resize-y disabled:cursor-not-allowed"
            {...props}
          />
        </div>

        {error ? (
          <p id={errorId} className="flex items-center gap-1.5 text-xs text-fault font-medium" role="alert">
            <AlertCircle className="h-3.5 w-3.5 shrink-0" />
            <span>{error}</span>
          </p>
        ) : helperText ? (
          <p id={helperId} className="text-xs text-text-muted">
            {helperText}
          </p>
        ) : null}
      </div>
    );
  }
);
Textarea.displayName = 'Textarea';
