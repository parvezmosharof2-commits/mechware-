'use client';

import React from 'react';
import { cn } from '../utils';
import { EmptySearchIllustration } from '../illustrations/EmptySearchIllustration';

export interface EmptyStateProps extends React.HTMLAttributes<HTMLDivElement> {
  illustration?: React.ReactNode;
  title: string;
  description?: string;
  action?: React.ReactNode;
  /** Heading level of the title; pick the level that follows the page's own headings (default 3). */
  headingLevel?: 2 | 3;
}

export const EmptyState: React.FC<EmptyStateProps> = ({
  illustration,
  title,
  description,
  action,
  headingLevel = 3,
  className,
  ...props
}) => {
  const Heading = `h${headingLevel}` as 'h2' | 'h3';
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center text-center p-8 md:p-12 font-sans select-none',
        className
      )}
      {...props}
    >
      <div className="mb-4 text-text-muted">
        {illustration || <EmptySearchIllustration className="w-40 h-40" />}
      </div>

      <Heading className="text-xl font-bold text-text mb-2 tracking-tight font-display">
        {title}
      </Heading>

      {description && (
        <p className="text-sm text-text-muted max-w-md mb-6 leading-relaxed">
          {description}
        </p>
      )}

      {action && <div className="flex items-center gap-3">{action}</div>}
    </div>
  );
};
