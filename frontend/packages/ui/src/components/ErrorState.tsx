'use client';

import React from 'react';
import { RotateCw, Home } from 'lucide-react';
import { cn } from '../utils';
import { SystemFaultIllustration } from '../illustrations/SystemFaultIllustration';
import { Button } from './Button';

export interface ErrorStateProps extends React.HTMLAttributes<HTMLDivElement> {
  code?: string | number;
  title?: string;
  description?: string;
  onRetry?: () => void;
  homeHref?: string;
}

export const ErrorState: React.FC<ErrorStateProps> = ({
  code = 500,
  title = 'Industrial Controller Telemetry Interrupted',
  description = 'Our telemetry system encountered an unexpected hardware handshake failure. The incident has been logged for our site reliability engineers.',
  onRetry,
  homeHref = '/',
  className,
  ...props
}) => {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center text-center p-8 md:p-12 font-sans select-none',
        className
      )}
      role="alert"
      {...props}
    >
      <div className="mb-4 text-fault">
        <SystemFaultIllustration className="w-40 h-40" />
      </div>

      <div className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-sm bg-fault/15 border border-fault/30 text-fault text-xs font-mono font-bold uppercase tracking-wider mb-3">
        FAULT CODE: {code}
      </div>

      <h3 className="text-xl md:text-2xl font-bold text-text mb-2 tracking-tight font-display">
        {title}
      </h3>

      <p className="text-sm text-text-muted max-w-lg mb-6 leading-relaxed">
        {description}
      </p>

      <div className="flex items-center gap-3">
        {onRetry && (
          <Button
            variant="accent"
            size="md"
            onClick={onRetry}
            leftIcon={<RotateCw className="h-4 w-4" />}
          >
            Retry Telemetry
          </Button>
        )}

        {homeHref && (
          <a href={homeHref}>
            <Button
              variant="secondary"
              size="md"
              leftIcon={<Home className="h-4 w-4" />}
            >
              Return to Catalog
            </Button>
          </a>
        )}
      </div>
    </div>
  );
};
