'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import { deskReasonProblem } from '@mechware/api-client';

/** A reason of 5 to 500 characters is asked for and checked here, before anything is sent. */
export function ReasonDialog({ open, title, description, label, confirmLabel, busy, serverProblem, onCancel, onSubmit }: {
  open: boolean; title: string; description: string; label: string; confirmLabel: string; busy: boolean; serverProblem?: string | null;
  onCancel: () => void; onSubmit: (reason: string) => void;
}) {
  const [reason, setReason] = React.useState('');
  const [problem, setProblem] = React.useState<string | null>(null);
  React.useEffect(() => { if (open) { setReason(''); setProblem(null); } }, [open]);
  const submit = () => {
    const p = deskReasonProblem(reason);
    if (p) return setProblem(p);
    setProblem(null);
    onSubmit(reason.trim());
  };
  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next && !busy) onCancel(); }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <Textarea label={label} value={reason} onChange={(e) => setReason(e.target.value)} error={problem ?? serverProblem ?? undefined} maxLength={500} rows={3} data-testid="reason-input" />
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel} disabled={busy}>Keep it as it is</Button>
          <Button type="button" variant="destructive" data-testid="reason-confirm" onClick={submit} isLoading={busy} disabled={busy}>{confirmLabel}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
