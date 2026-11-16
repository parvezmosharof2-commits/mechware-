'use client';

import * as React from 'react';
import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Input } from '@mechware/ui';
import { isStepUpCodeShape } from '@mechware/api-client';

/**
 * Asks for the 6-digit authenticator code right before a sensitive action. The parent runs the action with the code and
 * reports a refusal through `error`; the dialog stays open and the box is emptied, because a code works once and a wrong or
 * used one needs the NEXT code. The code is never kept after the call.
 */
export function StepUpDialog(props: {
  open: boolean;
  title: string;
  description: React.ReactNode;
  confirmLabel: string;
  busy: boolean;
  error: string | null;
  onSubmit: (code: string) => Promise<void>;
  onCancel: () => void;
}) {
  const { open, busy } = props;
  const [code, setCode] = React.useState('');
  const [local, setLocal] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (open) { setCode(''); setLocal(null); }
  }, [open]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!isStepUpCodeShape(code)) return setLocal('Enter the 6-digit code your authenticator app shows now.');
    setLocal(null);
    const value = code.trim();
    setCode('');
    await props.onSubmit(value);
  };

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next && !busy) props.onCancel(); }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{props.title}</DialogTitle>
          <DialogDescription>{props.description}</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} noValidate className="space-y-3" aria-label="Step-up confirmation">
          {props.error && <div role="alert" data-testid="stepup-error" className="rounded-md border border-fault bg-bg-subtle p-3 text-sm font-medium text-fault">{props.error}</div>}
          <Input
            label="Authenticator code"
            helperText="The 6 digits your authenticator app shows now. Each code works once; after a refusal wait for the next one. Five wrong codes sign you out."
            value={code}
            onChange={(e) => setCode(e.target.value)}
            error={local ?? undefined}
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            disabled={busy}
          />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={props.onCancel} disabled={busy}>Cancel</Button>
            <Button type="submit" isLoading={busy} disabled={busy}>{props.confirmLabel}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
