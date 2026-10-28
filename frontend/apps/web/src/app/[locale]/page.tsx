import { useTranslations } from 'next-intl';

export default function HomePage() {
  const t = useTranslations('Common');

  return (
    <main className="flex min-h-screen flex-col items-center justify-center p-6 text-center">
      <div className="max-w-4xl mx-auto space-y-8">
        <div className="inline-flex items-center gap-2 px-4 py-2 rounded-full border border-surface-border bg-bg-subtle text-accent text-sm font-semibold tracking-wide">
          <span className="w-2 h-2 rounded-full bg-status-online animate-pulse" />
          {t('systemHealthy')} · .NET 10 & Next.js 15 Active
        </div>

        <h1 className="text-4xl md:text-6xl font-display font-extrabold tracking-tight text-text">
          {t('brandName')} — <span className="text-accent">{t('tagline')}</span>
        </h1>

        <p className="text-lg md:text-xl text-text-muted max-w-2xl mx-auto">
          {t('subtitle')}
        </p>

        <div className="flex flex-wrap items-center justify-center gap-4 pt-4">
          <button className="px-6 py-3 rounded-md bg-accent text-accent-fg font-semibold hover:bg-accent-hover transition-colors shadow-lg shadow-accent/20">
            {t('exploreCatalog')}
          </button>
          <button className="px-6 py-3 rounded-md border border-surface-border bg-bg-subtle text-text hover:bg-surface transition-colors font-medium">
            {t('requestQuote')}
          </button>
        </div>

        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 pt-12 text-left">
          <div className="p-4 rounded-lg border border-surface-border bg-bg-subtle">
            <div className="text-accent font-bold text-xl">100+</div>
            <div className="text-xs text-text-muted">Automation Projects Delivered</div>
          </div>
          <div className="p-4 rounded-lg border border-surface-border bg-bg-subtle">
            <div className="text-accent font-bold text-xl">30+</div>
            <div className="text-xs text-text-muted">Tier-1 Industrial Clients</div>
          </div>
          <div className="p-4 rounded-lg border border-surface-border bg-bg-subtle">
            <div className="text-accent font-bold text-xl">12</div>
            <div className="text-xs text-text-muted">Key Manufacturing Sectors</div>
          </div>
          <div className="p-4 rounded-lg border border-surface-border bg-bg-subtle">
            <div className="text-accent font-bold text-xl">100%</div>
            <div className="text-xs text-text-muted">Made in BD Engineering</div>
          </div>
        </div>
      </div>
    </main>
  );
}
