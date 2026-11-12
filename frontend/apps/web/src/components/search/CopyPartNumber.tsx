'use client';

import * as React from 'react';
import { Check, Copy } from 'lucide-react';

export interface CopyPartNumberProps {
  value: string;
  label: string;
  copiedLabel: string;
}

/** Copies a part number; announces "Copied" through a polite live region. */
export function CopyPartNumber({ value, label, copiedLabel }: CopyPartNumberProps) {
  const [copied, setCopied] = React.useState(false);
  const timer = React.useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  React.useEffect(() => () => clearTimeout(timer.current), []);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 2000);
    } catch {
      /* clipboard unavailable (insecure context / denied): stay silent rather than claim success */
    }
  };

  return (
    <>
      <button
        type="button"
        onClick={copy}
        aria-label={label}
        className="relative z-10 inline-flex h-8 w-8 items-center justify-center rounded-sm text-text-muted hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
      >
        {copied ? <Check className="h-4 w-4" aria-hidden="true" /> : <Copy className="h-4 w-4" aria-hidden="true" />}
      </button>
      <span role="status" aria-live="polite" className="sr-only">
        {copied ? copiedLabel : ''}
      </span>
    </>
  );
}
