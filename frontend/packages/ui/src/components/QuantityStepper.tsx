'use client';

import React, { useState, useEffect, useRef } from 'react';
import { Minus, Plus } from 'lucide-react';
import { cn } from '../utils';

export interface QuantityStepperProps {
  value?: number;
  defaultValue?: number;
  min?: number;
  max?: number;
  step?: number;
  onChange?: (val: number) => void;
  disabled?: boolean;
  size?: 'sm' | 'md' | 'lg';
  className?: string;
  debounceMs?: number;
}

export const QuantityStepper: React.FC<QuantityStepperProps> = ({
  value,
  defaultValue = 1,
  min = 1,
  max = 999,
  step = 1,
  onChange,
  disabled = false,
  size = 'md',
  className,
  debounceMs = 300,
}) => {
  const [internalValue, setInternalValue] = useState<number>(value ?? defaultValue);
  const currentVal = value !== undefined ? value : internalValue;
  const timeoutRef = useRef<NodeJS.Timeout | null>(null);

  useEffect(() => {
    if (value !== undefined) {
      setInternalValue(value);
    }
  }, [value]);

  const triggerChange = (newVal: number) => {
    if (value === undefined) {
      setInternalValue(newVal);
    }
    if (timeoutRef.current) clearTimeout(timeoutRef.current);
    timeoutRef.current = setTimeout(() => {
      onChange?.(newVal);
    }, debounceMs);
  };

  const handleIncrement = () => {
    if (disabled || currentVal >= max) return;
    const next = Math.min(currentVal + step, max);
    triggerChange(next);
  };

  const handleDecrement = () => {
    if (disabled || currentVal <= min) return;
    const next = Math.max(currentVal - step, min);
    triggerChange(next);
  };

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const raw = e.target.value.replace(/\D/g, '');
    if (raw === '') {
      triggerChange(min);
      return;
    }
    const parsed = parseInt(raw, 10);
    const clamped = Math.max(min, Math.min(parsed, max));
    triggerChange(clamped);
  };

  const sizeClasses = {
    sm: {
      btn: 'h-8 w-8 text-xs',
      input: 'h-8 w-10 text-xs',
      icon: 'h-3.5 w-3.5',
    },
    md: {
      btn: 'h-10 w-10 text-sm',
      input: 'h-10 w-12 text-sm',
      icon: 'h-4 w-4',
    },
    lg: {
      btn: 'h-12 w-12 text-base',
      input: 'h-12 w-16 text-base',
      icon: 'h-5 w-5',
    },
  };

  const sc = sizeClasses[size];

  return (
    <div
      className={cn(
        'inline-flex items-center rounded-md border border-surface-border bg-bg-muted p-0.5 select-none font-sans',
        disabled && 'opacity-50 cursor-not-allowed',
        className
      )}
      role="group"
      aria-label="Quantity selector"
    >
      <button
        type="button"
        disabled={disabled || currentVal <= min}
        onClick={handleDecrement}
        className={cn(
          'flex items-center justify-center rounded-sm text-text-muted hover:bg-surface hover:text-text active:scale-95 transition-all outline-none disabled:opacity-40 disabled:pointer-events-none',
          sc.btn
        )}
        aria-label="Decrease quantity"
      >
        <Minus className={sc.icon} />
      </button>

      <input
        type="text"
        inputMode="numeric"
        pattern="[0-9]*"
        value={currentVal}
        disabled={disabled}
        onChange={handleInputChange}
        className={cn(
          'bg-transparent text-center font-semibold text-text tabular-nums outline-none border-none p-0 focus:text-accent',
          sc.input
        )}
        aria-label="Quantity"
      />

      <button
        type="button"
        disabled={disabled || currentVal >= max}
        onClick={handleIncrement}
        className={cn(
          'flex items-center justify-center rounded-sm text-text-muted hover:bg-surface hover:text-text active:scale-95 transition-all outline-none disabled:opacity-40 disabled:pointer-events-none',
          sc.btn
        )}
        aria-label="Increase quantity"
      >
        <Plus className={sc.icon} />
      </button>
    </div>
  );
};
