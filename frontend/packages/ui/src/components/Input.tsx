'use client';

import React from 'react';
import { AlertCircle } from 'lucide-react';
import { cn } from '../utils';

export interface InputProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'prefix'> {
  label?: string;
  helperText?: string;
  error?: string;
  prefix?: React.ReactNode;
  suffix?: React.ReactNode;
  maxLength?: number;
  showCharCount?: boolean;
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(
  (
    {
      className,
      type = 'text',
      id,
      label,
      helperText,
      error,
      prefix,
      suffix,
      maxLength,
      showCharCount,
      disabled,
      value,
      defaultValue,
      onChange,
      ...props
    },
    ref
  ) => {
    const generatedId = React.useId();
    const inputId = id || generatedId;
    const errorId = `${inputId}-error`;
    const helperId = `${inputId}-helper`;

    const [currentLength, setCurrentLength] = React.useState<number>(() => {
      if (typeof value === 'string') return value.length;
      if (typeof defaultValue === 'string') return defaultValue.length;
      return 0;
    });

    const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
      setCurrentLength(e.target.value.length);
      onChange?.(e);
    };

    return (
      <div className="w-full flex flex-col gap-1.5 font-sans">
        {label && (
          <div className="flex justify-between items-center text-sm font-medium text-text">
            <label htmlFor={inputId} className="cursor-pointer">
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
            'relative flex items-center w-full min-h-[44px] rounded-md border bg-bg-muted transition-colors duration-fast overflow-hidden',
            error
              ? 'border-fault focus-within:ring-2 focus-within:ring-fault'
              : 'border-surface-border-strong focus-within:border-accent focus-within:ring-2 focus-within:ring-accent focus-within:ring-offset-2 focus-within:ring-offset-bg',
            disabled && 'opacity-50 cursor-not-allowed bg-bg-subtle',
            className
          )}
        >
          {prefix && (
            <div className="flex items-center pl-3.5 pr-2 text-text-muted select-none shrink-0 text-sm">
              {prefix}
            </div>
          )}

          <input
            ref={ref}
            id={inputId}
            type={type}
            disabled={disabled}
            value={value}
            defaultValue={defaultValue}
            maxLength={maxLength}
            onChange={handleChange}
            aria-invalid={Boolean(error)}
            aria-describedby={
              error ? errorId : helperText ? helperId : undefined
            }
            className="w-full bg-transparent px-3.5 py-2.5 text-text text-sm placeholder:text-text-subtle outline-none border-none disabled:cursor-not-allowed"
            {...props}
          />

          {suffix && (
            <div className="flex items-center pr-3.5 pl-2 text-text-muted select-none shrink-0 text-sm">
              {suffix}
            </div>
          )}
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
Input.displayName = 'Input';
