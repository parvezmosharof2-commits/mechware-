'use client';

import React from 'react';
import { Check, AlertCircle } from 'lucide-react';
import { cn } from '../utils';

export interface StepItem {
  id: string | number;
  title: string;
  description?: string;
  status?: 'completed' | 'active' | 'upcoming' | 'error';
}

export interface StepperProps {
  steps: StepItem[];
  activeStep?: number;
  orientation?: 'horizontal' | 'vertical';
  className?: string;
  onStepClick?: (index: number) => void;
}

export const Stepper: React.FC<StepperProps> = ({
  steps,
  activeStep = 0,
  orientation = 'horizontal',
  className,
  onStepClick,
}) => {
  return (
    <div
      className={cn(
        'w-full font-sans',
        orientation === 'horizontal'
          ? 'flex items-center justify-between'
          : 'flex flex-col space-y-4',
        className
      )}
      aria-label="Progress steps"
    >
      {steps.map((step, idx) => {
        const stepStatus =
          step.status ??
          (idx < activeStep ? 'completed' : idx === activeStep ? 'active' : 'upcoming');

        const isCompleted = stepStatus === 'completed';
        const isActive = stepStatus === 'active';
        const isError = stepStatus === 'error';

        return (
          <div
            key={step.id}
            className={cn(
              'relative flex items-center',
              orientation === 'horizontal' ? 'flex-1 last:flex-none' : 'w-full'
            )}
          >
            {/* Step Node */}
            <button
              type="button"
              disabled={!onStepClick || stepStatus === 'upcoming'}
              onClick={() => onStepClick?.(idx)}
              className={cn(
                'group flex items-center gap-3 outline-none transition-colors select-none text-left',
                onStepClick && stepStatus !== 'upcoming' ? 'cursor-pointer' : 'cursor-default'
              )}
            >
              <div
                className={cn(
                  'flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-xs font-bold transition-all duration-fast',
                  isCompleted && 'bg-status-online text-bg font-extrabold shadow-sm',
                  isActive &&
                    'border-2 border-accent bg-accent/20 text-accent shadow-glow-cyan-sm',
                  isError && 'bg-fault text-white shadow-glow-fault',
                  stepStatus === 'upcoming' &&
                    'border border-surface-border bg-bg-muted text-text-subtle'
                )}
              >
                {isCompleted ? (
                  <Check className="h-4 w-4 stroke-[3]" />
                ) : isError ? (
                  <AlertCircle className="h-4 w-4" />
                ) : (
                  <span>{idx + 1}</span>
                )}
              </div>

              <div className="flex flex-col">
                <span
                  className={cn(
                    'text-sm font-semibold leading-tight transition-colors',
                    isActive ? 'text-accent' : isCompleted ? 'text-text' : 'text-text-muted'
                  )}
                >
                  {step.title}
                </span>
                {step.description && (
                  <span className="text-xs text-text-subtle mt-0.5">{step.description}</span>
                )}
              </div>
            </button>

            {/* Connecting Line (Horizontal) */}
            {orientation === 'horizontal' && idx < steps.length - 1 && (
              <div
                className={cn(
                  'h-0.5 flex-1 mx-4 transition-colors',
                  idx < activeStep ? 'bg-status-online' : 'bg-surface-border'
                )}
              />
            )}
          </div>
        );
      })}
    </div>
  );
};
