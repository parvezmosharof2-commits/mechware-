'use client';

import React from 'react';
import * as CheckboxPrimitive from '@radix-ui/react-checkbox';
import { Check } from 'lucide-react';
import { cn } from '../utils';

export interface CheckboxProps
  extends React.ComponentPropsWithoutRef<typeof CheckboxPrimitive.Root> {
  label?: React.ReactNode;
  description?: React.ReactNode;
  error?: string;
}

export const Checkbox = React.forwardRef<
  React.ElementRef<typeof CheckboxPrimitive.Root>,
  CheckboxProps
>(({ className, label, description, error, id, ...props }, ref) => {
  const generatedId = React.useId();
  const checkboxId = id || generatedId;

  return (
    <div className="flex flex-col gap-1 font-sans">
      <div className="flex items-start gap-2.5">
        <CheckboxPrimitive.Root
          ref={ref}
          id={checkboxId}
          className={cn(
            'peer h-4 w-4 shrink-0 rounded-xs border transition-all duration-fast outline-none select-none mt-0.5',
            'focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg',
            'disabled:cursor-not-allowed disabled:opacity-50',
            'data-[state=checked]:bg-accent data-[state=checked]:border-accent data-[state=checked]:text-accent-fg',
            error
              ? 'border-fault'
              : 'border-surface-border-strong bg-bg-muted hover:border-accent',
            className
          )}
          {...props}
        >
          <CheckboxPrimitive.Indicator className="flex items-center justify-center text-current">
            <Check className="h-3.5 w-3.5 stroke-[3]" />
          </CheckboxPrimitive.Indicator>
        </CheckboxPrimitive.Root>

        {(label || description) && (
          <div className="flex flex-col text-sm leading-tight select-none">
            {label && (
              <label
                htmlFor={checkboxId}
                className="font-medium text-text cursor-pointer hover:text-accent transition-colors"
              >
                {label}
              </label>
            )}
            {description && (
              <span className="text-xs text-text-muted mt-0.5">{description}</span>
            )}
          </div>
        )}
      </div>

      {error && <p className="text-xs text-fault font-medium pl-6">{error}</p>}
    </div>
  );
});
Checkbox.displayName = CheckboxPrimitive.Root.displayName;
