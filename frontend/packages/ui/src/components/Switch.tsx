'use client';

import React from 'react';
import * as SwitchPrimitive from '@radix-ui/react-switch';
import { cn } from '../utils';

export interface SwitchProps
  extends React.ComponentPropsWithoutRef<typeof SwitchPrimitive.Root> {
  label?: React.ReactNode;
  description?: React.ReactNode;
}

export const Switch = React.forwardRef<
  React.ElementRef<typeof SwitchPrimitive.Root>,
  SwitchProps
>(({ className, label, description, id, ...props }, ref) => {
  const generatedId = React.useId();
  const switchId = id || generatedId;

  return (
    <div className="flex items-center justify-between gap-3 font-sans">
      {(label || description) && (
        <div className="flex flex-col select-none">
          {label && (
            <label
              htmlFor={switchId}
              className="text-sm font-medium text-text cursor-pointer hover:text-accent transition-colors"
            >
              {label}
            </label>
          )}
          {description && (
            <span className="text-xs text-text-muted mt-0.5">{description}</span>
          )}
        </div>
      )}

      <SwitchPrimitive.Root
        id={switchId}
        className={cn(
          'peer inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full border-2 border-transparent transition-colors duration-fast outline-none',
          'focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg',
          'disabled:cursor-not-allowed disabled:opacity-50',
          'data-[state=checked]:bg-accent data-[state=unchecked]:bg-bg-muted border-surface-border',
          className
        )}
        {...props}
        ref={ref}
      >
        <SwitchPrimitive.Thumb
          className={cn(
            'pointer-events-none block h-5 w-5 rounded-full bg-white shadow-lg ring-0 transition-transform duration-fast',
            'data-[state=checked]:translate-x-5 data-[state=checked]:bg-bg data-[state=unchecked]:translate-x-0'
          )}
        />
      </SwitchPrimitive.Root>
    </div>
  );
});
Switch.displayName = SwitchPrimitive.Root.displayName;
