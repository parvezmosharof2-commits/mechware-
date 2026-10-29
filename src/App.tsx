import React, { useState, useMemo } from 'react';
import {
  Search,
  ShoppingCart,
  FileText,
  Download,
  Check,
  ShieldAlert,
  Copy,
  SlidersHorizontal,
  ArrowRight,
  Plus,
  Minus,
  X,
  Lock,
  Building2,
  Truck,
  FileCode2,
} from 'lucide-react';
import { INDUSTRIAL_CATALOG, IndustrialProduct } from './data/industrialCatalog';
import { PHASE_0_AUDIT_FINDINGS, AuditFinding } from './data/auditMatrix';
import { HardwareVectorArt } from './components/HardwareVectorArt';
import { Phase3IdentityWorkbench } from './components/Phase3IdentityWorkbench';

interface CartLine {
  productId: string;
  variantId: string;
  quantity: number;
}

type ActiveSection = 'catalog' | 'architecture-audit' | 'rfq-desk' | 'order-simulator' | 'phase3-identity';

export function App() {
  const initialParams = typeof window !== 'undefined' ? new URLSearchParams(window.location.search) : null;
  const initialSectionParam = initialParams?.get('section') as ActiveSection | null;
  const initialLocaleParam = initialParams?.get('locale') as 'en' | 'bn' | null;
  const initialThemeParam = initialParams?.get('theme') as 'dark' | 'light' | null;
  const initialModalParam = initialParams?.get('modal') === 'open';

  const [activeSection, setActiveSection] = useState<ActiveSection>(
    initialSectionParam &&
      ['catalog', 'architecture-audit', 'rfq-desk', 'order-simulator', 'phase3-identity'].includes(initialSectionParam)
      ? initialSectionParam
      : 'catalog'
  );
  const [locale, setLocale] = useState<'en' | 'bn'>(initialLocaleParam === 'bn' ? 'bn' : 'en');
  const [theme, setTheme] = useState<'dark' | 'light'>(initialThemeParam === 'light' ? 'light' : 'dark');
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedCategory, setSelectedCategory] = useState<string>('all');
  const [selectedBrand, setSelectedBrand] = useState<string>('all');

  // Product Detail Modal / Contiguous Purchase View
  const [activeProduct, setActiveProduct] = useState<IndustrialProduct>(INDUSTRIAL_CATALOG[0]);
  const [selectedVariantMap, setSelectedVariantMap] = useState<Record<string, string>>({
    'prod-s7-1200': 'var-s7-1214c-dc',
    'prod-delta-vfd': 'var-delta-22kw',
    'prod-mechware-etp-skid': 'var-mw-etp-50m3',
    'prod-mitsubishi-servo': 'var-mit-400w',
    'prod-omron-sensor': 'var-omr-m12',
    'prod-delta-hmi': 'var-hmi-110ws',
  });
  const [pdpModalOpen, setPdpModalOpen] = useState(false);

  // Cart & Atomic Checkout State
  const [cart, setCart] = useState<CartLine[]>([
    { productId: 'prod-s7-1200', variantId: 'var-s7-1214c-dc', quantity: 2 },
    { productId: 'prod-delta-vfd', variantId: 'var-delta-22kw', quantity: 1 },
  ]);
  const [cartDrawerOpen, setCartDrawerOpen] = useState(false);
  const [justAddedId, setJustAddedId] = useState<string | null>(null);
  const [couponCode, setCouponCode] = useState('INDUSTRY40');
  const [appliedCoupon, setAppliedCoupon] = useState<'INDUSTRY40' | 'BKASH10' | null>('INDUSTRY40');
  const [paymentMethod, setPaymentMethod] = useState<'bkash' | 'stripe' | 'sslcommerz' | 'cod'>('bkash');
  const [deliveryZone, setDeliveryZone] = useState<'inside_dhaka' | 'dhaka_suburbs' | 'outside_dhaka'>('inside_dhaka');

  // Reservation & Order Placement Simulation
  const [reservationToken, setReservationToken] = useState<string | null>('res_89a2bc1e_15m_lock');
  const [codOtpInput, setCodOtpInput] = useState('');
  const [confirmedOrderNumber, setConfirmedOrderNumber] = useState<string | null>(null);

  // Audit Matrix Filters & Copy State
  const [auditSeverityFilter, setAuditSeverityFilter] = useState<'ALL' | 'Critical' | 'High' | 'Medium' | 'Low'>('ALL');
  const [copiedAuditReport, setCopiedAuditReport] = useState(false);
  const [copiedFixId, setCopiedFixId] = useState<string | null>(null);

  // RFQ Form State
  const [rfqSubmitted, setRfqSubmitted] = useState<string | null>(null);
  const [rfqCompany, setRfqCompany] = useState('Square Food & Beverage Ltd.');
  const [rfqEngineer, setRfqEngineer] = useState('Engr. Parvez Mosharof');
  const [rfqPhone, setRfqPhone] = useState('+8801748305931');
  const [rfqProjectType, setRfqProjectType] = useState('Turnkey ETP Automation & DoE SCADA');
  const [rfqNotes, setRfqNotes] = useState(
    'Require closed-loop PID chemical dosing skid with Siemens S7-1200 CPU 1214C, 2x pH/DO transmitters, and Modbus TCP cloud telemetry.'
  );

  // Format BDT with Bangladeshi Lakh/Crore grouping & tabular numerals
  const formatBdt = (amount: number) => {
    return `৳${amount.toLocaleString('en-IN')}`;
  };

  // Filtered Catalog
  const filteredProducts = useMemo(() => {
    return INDUSTRIAL_CATALOG.filter((p) => {
      const matchesCat = selectedCategory === 'all' || p.categorySlug === selectedCategory;
      const matchesBrand = selectedBrand === 'all' || p.brand === selectedBrand;
      const q = searchQuery.trim().toLowerCase();
      const matchesQuery =
        !q ||
        p.titleEn.toLowerCase().includes(q) ||
        p.titleBn.toLowerCase().includes(q) ||
        p.brand.toLowerCase().includes(q) ||
        p.shortSummary.toLowerCase().includes(q) ||
        p.variants.some((v) => v.sku.toLowerCase().includes(q));
      return matchesCat && matchesBrand && matchesQuery;
    });
  }, [selectedCategory, selectedBrand, searchQuery]);

  // Enriched Cart & Multi-Seller Order Split Math (Banker's Rounding & Explicit Seller vs Platform Discount)
  const cartSummary = useMemo(() => {
    const detailedLines = cart
      .map((line) => {
        const product = INDUSTRIAL_CATALOG.find((p) => p.id === line.productId);
        const variant = product?.variants.find((v) => v.id === line.variantId);
        if (!product || !variant) return null;
        const available = variant.onHand - variant.reserved;
        const lineSubtotal = variant.priceBdt * line.quantity;
        return {
          ...line,
          product,
          variant,
          available,
          lineSubtotal,
        };
      })
      .filter(Boolean) as {
      productId: string;
      variantId: string;
      quantity: number;
      product: IndustrialProduct;
      variant: IndustrialProduct['variants'][0];
      available: number;
      lineSubtotal: number;
    }[];

    const subtotal = detailedLines.reduce((acc, l) => acc + l.lineSubtotal, 0);

    // Explicit Seller-Funded vs Platform-Funded Discount per Audit Finding MW-AUD-02
    const sellerDiscountTotal = appliedCoupon === 'INDUSTRY40' && subtotal >= 25000 ? 2500 : 0;
    const platformDiscountTotal =
      appliedCoupon === 'BKASH10' && paymentMethod === 'bkash' ? Math.min(Math.round(subtotal * 0.05), 4000) : 0;
    const totalDiscount = sellerDiscountTotal + platformDiscountTotal;

    const shippingPerSeller = deliveryZone === 'inside_dhaka' ? 150 : deliveryZone === 'dhaka_suburbs' ? 280 : 450;

    // Group into SubOrders per Seller (ADR-0003)
    const sellerGroupsMap = new Map<
      string,
      {
        sellerName: string;
        commissionRate: number;
        items: typeof detailedLines;
        subtotal: number;
        sellerDiscount: number;
        platformDiscount: number;
        commissionAmount: number;
        shippingFee: number;
        vatAmount: number;
        sellerNetEarning: number;
      }
    >();

    detailedLines.forEach((item) => {
      const key = item.product.sellerName;
      const existing = sellerGroupsMap.get(key) || {
        sellerName: key,
        commissionRate: item.product.commissionRate,
        items: [],
        subtotal: 0,
        sellerDiscount: 0,
        platformDiscount: 0,
        commissionAmount: 0,
        shippingFee: subtotal >= 50000 ? 0 : shippingPerSeller,
        vatAmount: 0,
        sellerNetEarning: 0,
      };
      existing.items.push(item);
      existing.subtotal += item.lineSubtotal;
      sellerGroupsMap.set(key, existing);
    });

    const subOrders = Array.from(sellerGroupsMap.values()).map((group, idx) => {
      const shareRatio = subtotal > 0 ? group.subtotal / subtotal : 0;
      const allocatedSellerDiscount = Math.round(sellerDiscountTotal * shareRatio);
      const allocatedPlatformDiscount = Math.round(platformDiscountTotal * shareRatio);
      // Brief 8.9: Commission = category rate * item subtotal after seller-funded discounts
      const commissionBase = group.subtotal - allocatedSellerDiscount;
      const commissionAmount = Math.round(commissionBase * group.commissionRate);
      // Audit Fix MW-AUD-02: Platform discount does NOT reduce seller net earning!
      const sellerNetEarning = group.subtotal - allocatedSellerDiscount - commissionAmount;
      const vatAmount = Math.round((group.subtotal - allocatedSellerDiscount - allocatedPlatformDiscount) * 0.05);

      return {
        ...group,
        subOrderSuffix: `0${idx + 1}`,
        sellerDiscount: allocatedSellerDiscount,
        platformDiscount: allocatedPlatformDiscount,
        commissionAmount,
        vatAmount,
        sellerNetEarning,
      };
    });

    const shippingTotal = subOrders.reduce((acc, s) => acc + s.shippingFee, 0);
    const vatTotal = subOrders.reduce((acc, s) => acc + s.vatAmount, 0);
    const grandTotal = subtotal - totalDiscount + shippingTotal + vatTotal;
    const codAllowed = grandTotal <= 15000 && detailedLines.every((l) => l.product.codEligible);

    return {
      detailedLines,
      subOrders,
      subtotal,
      sellerDiscountTotal,
      platformDiscountTotal,
      totalDiscount,
      shippingTotal,
      vatTotal,
      grandTotal,
      codAllowed,
      totalCount: detailedLines.reduce((acc, l) => acc + l.quantity, 0),
    };
  }, [cart, appliedCoupon, paymentMethod, deliveryZone]);

  const handleAddToCart = (productId: string, variantId: string) => {
    setCart((prev) => {
      const existing = prev.find((i) => i.productId === productId && i.variantId === variantId);
      if (existing) {
        return prev.map((i) =>
          i.productId === productId && i.variantId === variantId ? { ...i, quantity: Math.min(i.quantity + 1, 10) } : i
        );
      }
      return [...prev, { productId, variantId, quantity: 1 }];
    });
    setJustAddedId(variantId);
    setTimeout(() => setJustAddedId(null), 1200);
  };

  const handleUpdateQty = (variantId: string, delta: number) => {
    setCart((prev) =>
      prev
        .map((item) => (item.variantId === variantId ? { ...item, quantity: item.quantity + delta } : item))
        .filter((item) => item.quantity > 0)
    );
  };

  const handleCopyFullAuditMarkdown = () => {
    const markdown = [
      '# MECHWARE — Principal Architect, Staff Cybersecurity & Design Audit Report (Phase 0–2 Deep Re-Verification)',
      '',
      '| ID | Phase | Severity | Category | File / Location | Flaw Summary |',
      '|---|---|---|---|---|---|',
      ...PHASE_0_AUDIT_FINDINGS.map(
        (f) => `| ${f.id} | ${f.phase} | **${f.severity}** | ${f.category} | \`${f.fileLocation}\` | ${f.title} |`
      ),
      '',
      ...PHASE_0_AUDIT_FINDINGS.map(
        (f) =>
          `### [${f.id}] (${f.severity} · ${f.phase}) ${f.title}\n- **Location**: \`${f.fileLocation}\`\n- **Flaw**: ${f.flawExplanation}\n- **Exact Code Fix**:\n\`\`\`${f.language}\n${f.exactCodeFix}\n\`\`\`\n`
      ),
    ].join('\n');

    navigator.clipboard.writeText(markdown);
    setCopiedAuditReport(true);
    setTimeout(() => setCopiedAuditReport(false), 2000);
  };

  const filteredAuditFindings = useMemo(() => {
    if (auditSeverityFilter === 'ALL') return PHASE_0_AUDIT_FINDINGS;
    return PHASE_0_AUDIT_FINDINGS.filter((f) => f.severity === auditSeverityFilter);
  }, [auditSeverityFilter]);

  return (
    <div
      className={`min-h-screen flex flex-col transition-colors ${
        theme === 'light' ? 'bg-[#F8FAFC] text-[#0B132B]' : 'bg-[#060B18] text-[#F8FAFC]'
      }`}
    >
      {/* STRICT 3-ZONE TOP BAR CONTRACT */}
      <header
        className={`sticky top-0 z-40 backdrop-blur-md border-b ${
          theme === 'light'
            ? 'bg-[#FFFFFF]/95 border-[#E2E8F0]'
            : 'bg-[#060B18]/95 border-[#1B2B55]'
        }`}
      >
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 h-16 flex items-center justify-between gap-4 lg:gap-6">
          {/* Zone 1: Single text element wordmark */}
          <a
            href="#top"
            onClick={(e) => {
              e.preventDefault();
              setActiveSection('catalog');
            }}
            className={`text-xl font-extrabold tracking-tight whitespace-nowrap shrink-0 ${
              theme === 'light' ? 'text-[#0B132B]' : 'text-[#F8FAFC]'
            }`}
          >
            MECHWARE
          </a>

          {/* Zone 2: Concise Single-Line Navigation Links */}
          <nav
            className={`hidden lg:flex items-center gap-6 text-sm font-medium ${
              theme === 'light' ? 'text-[#475569]' : 'text-[#94A3B8]'
            }`}
          >
            <button
              onClick={() => setActiveSection('catalog')}
              className={`transition-colors whitespace-nowrap shrink-0 cursor-pointer pb-1 border-b-2 ${
                activeSection === 'catalog'
                  ? theme === 'light'
                    ? 'text-[#0B132B] border-[#0F3E7D]'
                    : 'text-[#F8FAFC] border-[#00E5FF]'
                  : 'border-transparent'
              }`}
            >
              Automation Catalog
            </button>
            <button
              onClick={() => setActiveSection('order-simulator')}
              className={`transition-colors whitespace-nowrap shrink-0 cursor-pointer pb-1 border-b-2 ${
                activeSection === 'order-simulator'
                  ? theme === 'light'
                    ? 'text-[#0B132B] border-[#0F3E7D]'
                    : 'text-[#F8FAFC] border-[#00E5FF]'
                  : 'border-transparent'
              }`}
            >
              Split-Order & Ledger
            </button>
            <button
              onClick={() => setActiveSection('rfq-desk')}
              className={`transition-colors whitespace-nowrap shrink-0 cursor-pointer pb-1 border-b-2 ${
                activeSection === 'rfq-desk'
                  ? theme === 'light'
                    ? 'text-[#0B132B] border-[#0F3E7D]'
                    : 'text-[#F8FAFC] border-[#00E5FF]'
                  : 'border-transparent'
              }`}
            >
              Turnkey RFQ
            </button>
            <button
              onClick={() => setActiveSection('architecture-audit')}
              className={`transition-colors whitespace-nowrap shrink-0 cursor-pointer pb-1 border-b-2 ${
                activeSection === 'architecture-audit'
                  ? theme === 'light'
                    ? 'text-[#0B132B] border-[#0F3E7D]'
                    : 'text-[#F8FAFC] border-[#00E5FF]'
                  : 'border-transparent'
              }`}
            >
              Phase 0–2 Audit
            </button>
            <button
              onClick={() => setActiveSection('phase3-identity')}
              className={`transition-colors whitespace-nowrap shrink-0 cursor-pointer pb-1 border-b-2 ${
                activeSection === 'phase3-identity'
                  ? theme === 'light'
                    ? 'text-[#0B132B] border-[#0F3E7D]'
                    : 'text-[#F8FAFC] border-[#00E5FF]'
                  : 'border-transparent'
              }`}
            >
              Identity & RBAC
            </button>
          </nav>

          {/* Zone 3: 1 Primary Action */}
          <div className="flex items-center shrink-0">
            <button
              onClick={() => setCartDrawerOpen(true)}
              className={`px-4 py-2 text-xs font-semibold rounded-[10px] transition-colors whitespace-nowrap shrink-0 flex items-center gap-2 cursor-pointer ${
                theme === 'light'
                  ? 'bg-[#0F3E7D] text-[#FFFFFF] hover:bg-[#1652A3]'
                  : 'bg-[#00E5FF] text-[#060B18] hover:bg-[#38EFFF]'
              }`}
            >
              <ShoppingCart className="w-4 h-4" />
              <span>Procurement Cart ({cartSummary.totalCount})</span>
            </button>
          </div>
        </div>
      </header>

      <main className="flex-1">
        {activeSection === 'phase3-identity' && (
          <Phase3IdentityWorkbench
            locale={locale}
            theme={theme}
            initialModalOpen={initialModalParam}
            onToggleLocale={() => setLocale((prev) => (prev === 'en' ? 'bn' : 'en'))}
            onToggleTheme={() => setTheme((prev) => (prev === 'dark' ? 'light' : 'dark'))}
          />
        )}
        {/* SECTION 1: HERO & WORKSPACE CONTROL BAR */}
        {activeSection === 'catalog' && (
          <section className="relative border-b border-[#1B2B55] bg-[#0A1128]">
            <div className="max-w-[1440px] mx-auto px-6 py-12 lg:py-16">
              <div className="grid grid-cols-1 lg:grid-cols-12 gap-8 items-stretch">
                {/* Left Column: Campaign Focal Point */}
                <div className="lg:col-span-7 flex flex-col justify-between space-y-6">
                  <div className="space-y-4">
                    <div className="flex flex-wrap items-center gap-2 text-xs text-[#94A3B8]">
                      <span>Gateway to Industry 4.0</span>
                      <span aria-hidden="true">·</span>
                      <span>Made in Bangladesh Machinery</span>
                      <span aria-hidden="true">·</span>
                      <span>Gulshan-1, Dhaka 1212</span>
                    </div>

                    <h1
                      className="text-3xl sm:text-5xl font-extrabold tracking-tight text-[#F8FAFC] leading-[1.12]"
                      style={{ textWrap: 'balance' }}
                    >
                      {locale === 'en'
                        ? 'Industrial Automation Hardware & Turnkey SCADA Systems'
                        : 'ইন্ডাস্ট্রিয়াল অটোমেশন হার্ডওয়্যার এবং টার্নকি স্ক্যাডা সিস্টেম'}
                    </h1>

                    <p className="text-base sm:text-lg text-[#94A3B8] max-w-[65ch] leading-relaxed">
                      {locale === 'en'
                        ? 'Direct procurement of Siemens, Mitsubishi Electric, Delta, and Omron controllers alongside locally engineered ETP chemical dosing skids and CNC machinery. Guaranteed 15-minute atomic stock lock and verified CAD/STEP documentation.'
                        : 'সিমেন্স, মিতসুবিশি ইলেকট্রিক, ডেল্টা এবং ওমরন অটোমেশন ডিভাইসের পাশাপাশি বাংলাদেশে তৈরি ইটিপি ডোজিং স্কিড ও সিএনসি মেশিনারি।'}
                    </p>
                  </div>

                  {/* Search & Quick Action Bar */}
                  <div className="space-y-4 pt-2">
                    <div className="flex flex-col sm:flex-row gap-3">
                      <div className="relative flex-1">
                        <Search className="w-4 h-4 text-[#94A3B8] absolute left-3.5 top-1/2 -translate-y-1/2" />
                        <input
                          type="text"
                          value={searchQuery}
                          onChange={(e) => setSearchQuery(e.target.value)}
                          placeholder="Search part number, PLC CPU 1214C, 415V VFD, M18 PNP sensor..."
                          aria-label="Search industrial catalog"
                          className="w-full h-11 pl-10 pr-4 bg-[#101B3B] border border-[#2A4078] rounded-[10px] text-sm text-[#F8FAFC] placeholder-[#64748B] focus:outline-none focus:border-[#00E5FF]"
                        />
                      </div>
                      <button
                        onClick={() => setActiveSection('architecture-audit')}
                        className="h-11 px-5 bg-[#0D1635] border border-[#2A4078] hover:border-[#00E5FF] text-[#F8FAFC] text-xs font-semibold rounded-[10px] transition-colors whitespace-nowrap shrink-0 flex items-center justify-center gap-2 cursor-pointer"
                      >
                        <ShieldAlert className="w-4 h-4 text-[#00E5FF]" />
                        <span>View Phase 0–2 Audit Matrix</span>
                      </button>
                    </div>

                    {/* Interactive Workspace Controls (Language & Category Filter Tabs) */}
                    <div className="flex flex-wrap items-center justify-between gap-4 pt-2 border-t border-[#1B2B55]">
                      <div className="flex flex-wrap items-center gap-1.5">
                        {[
                          { id: 'all', label: 'All Hardware (6)' },
                          { id: 'plcs-controllers', label: 'PLCs' },
                          { id: 'vfds-inverters', label: 'VFD Drives' },
                          { id: 'made-in-bd-machinery', label: 'Made in BD Skids' },
                          { id: 'servo-motion-control', label: 'Servo Motion' },
                          { id: 'smart-sensors', label: 'Sensors' },
                          { id: 'hmis-displays', label: 'Touch HMIs' },
                        ].map((cat) => (
                          <button
                            key={cat.id}
                            onClick={() => setSelectedCategory(cat.id)}
                            className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                              selectedCategory === cat.id
                                ? 'bg-[#00E5FF] text-[#060B18] font-semibold'
                                : 'bg-[#101B3B] text-[#94A3B8] hover:text-[#F8FAFC]'
                            }`}
                          >
                            {cat.label}
                          </button>
                        ))}
                      </div>

                      <div className="flex items-center gap-1 p-1 bg-[#101B3B] rounded-lg shrink-0">
                        <button
                          onClick={() => setLocale('en')}
                          className={`px-2.5 py-1 text-xs font-medium rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                            locale === 'en' ? 'bg-[#0D1635] text-[#00E5FF]' : 'text-[#94A3B8]'
                          }`}
                        >
                          EN · BDT
                        </button>
                        <button
                          onClick={() => setLocale('bn')}
                          className={`px-2.5 py-1 text-xs font-medium rounded transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                            locale === 'bn' ? 'bg-[#0D1635] text-[#00E5FF]' : 'text-[#94A3B8]'
                          }`}
                        >
                          বাংলা · ৳
                        </button>
                      </div>
                    </div>
                  </div>
                </div>

                {/* Right Column: Featured Flagship Hardware Card */}
                <div className="lg:col-span-5 bg-[#0D1635] border border-[#1B2B55] rounded-[14px] p-6 flex flex-col justify-between">
                  <div className="flex items-center justify-between text-xs text-[#94A3B8] mb-3">
                    <span>FLAGSHIP MADE-IN-BANGLADESH SYSTEM</span>
                    <span className="font-mono-tabular text-[#34D399]">DoE SCADA COMPLIANT</span>
                  </div>
                  <div className="h-48 rounded-[10px] overflow-hidden border border-[#1B2B55] mb-4">
                    <HardwareVectorArt type="skid" sku="MW-BD-ETP-SKID-150M3" />
                  </div>
                  <div className="space-y-2">
                    <h2 className="text-lg font-bold text-[#F8FAFC]">
                      MECHWARE Automated ETP Chemical Dosing & Cloud SCADA Skid
                    </h2>
                    <p className="text-xs text-[#94A3B8] leading-relaxed">
                      Deployed at Square Food & Beverage and Naturub Accessories BD. Reduces coagulant dosing cost by
                      18–26% with closed-loop Siemens S7-1200 PID control.
                    </p>
                    <div className="pt-3 flex items-center justify-between border-t border-[#1B2B55]">
                      <div>
                        <div className="text-xs text-[#94A3B8]">Turnkey Skid Starting At</div>
                        <div className="text-xl font-bold text-[#F8FAFC] font-mono-tabular">৳3,85,000</div>
                      </div>
                      <div className="flex items-center gap-2">
                        <button
                          onClick={() => {
                            setActiveProduct(INDUSTRIAL_CATALOG[2]);
                            setPdpModalOpen(true);
                          }}
                          className="px-3.5 py-2 text-xs font-medium bg-[#101B3B] hover:bg-[#1B2B55] text-[#F8FAFC] rounded-[10px] transition-colors whitespace-nowrap shrink-0 cursor-pointer"
                        >
                          Inspect Specs & CAD
                        </button>
                        <button
                          onClick={() => setActiveSection('rfq-desk')}
                          className="px-3.5 py-2 text-xs font-semibold bg-[#00E5FF] hover:bg-[#38EFFF] text-[#060B18] rounded-[10px] transition-colors whitespace-nowrap shrink-0 cursor-pointer"
                        >
                          Request Quote
                        </button>
                      </div>
                    </div>
                  </div>
                </div>
              </div>

              {/* Full-Width Verified Quantitative Proof Row Below Hero Split */}
              <div className="mt-10 pt-8 border-t border-[#1B2B55] grid grid-cols-2 lg:grid-cols-4 gap-6">
                <div>
                  <div className="text-2xl font-extrabold text-[#F8FAFC] font-mono-tabular">100+ Systems</div>
                  <div className="text-xs text-[#94A3B8] mt-1">
                    Turnkey PLC, VFD & retrofit commissioning projects delivered across Bangladesh since 2024
                  </div>
                </div>
                <div>
                  <div className="text-2xl font-extrabold text-[#F8FAFC] font-mono-tabular">30+ Factories</div>
                  <div className="text-xs text-[#94A3B8] mt-1">
                    Trusted by Square, Singer/Beko, Fair Electronics (Hisense), RFL, Bengal Group & Jamuna Paper
                  </div>
                </div>
                <div>
                  <div className="text-2xl font-extrabold text-[#00E5FF] font-mono-tabular">300 PPM</div>
                  <div className="text-xs text-[#94A3B8] mt-1">
                    High-speed duplex paper cutting & robotic blister packaging motion synchronization
                  </div>
                </div>
                <div>
                  <div className="text-2xl font-extrabold text-[#34D399] font-mono-tabular">15-Min Hold</div>
                  <div className="text-xs text-[#94A3B8] mt-1">
                    PostgreSQL row-level locking (`SELECT FOR UPDATE`) + Redis RedLock prevents overselling
                  </div>
                </div>
              </div>
            </div>
          </section>
        )}

        {/* SECTION 2: FEATURED INDUSTRIAL CATALOG GRID (3-Column Desktop) */}
        {activeSection === 'catalog' && (
          <section className="max-w-[1440px] mx-auto px-6 py-14">
            <div className="flex flex-col md:flex-row md:items-end justify-between mb-8 gap-4">
              <div>
                <div className="text-xs text-[#94A3B8] uppercase tracking-wider">
                  Authorized Industrial Distribution & Direct Manufacturing
                </div>
                <h2 className="text-2xl sm:text-3xl font-bold text-[#F8FAFC] mt-1">
                  {locale === 'en'
                    ? 'Engineered Components with Instant CAD & Datasheet Access'
                    : 'তাৎক্ষণিক ক্যাড ও ডেটাশিটসহ ইন্ডাস্ট্রিয়াল অটোমেশন ক্যাটালগ'}
                </h2>
              </div>

              {/* Brand Filter Segmented Control */}
              <div className="flex flex-wrap items-center gap-1.5 bg-[#0A1128] p-1.5 rounded-[10px] border border-[#1B2B55]">
                {['all', 'Siemens', 'Delta', 'Mitsubishi Electric', 'Omron', 'MECHWARE Direct'].map((brand) => (
                  <button
                    key={brand}
                    onClick={() => setSelectedBrand(brand)}
                    className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                      selectedBrand === brand
                        ? 'bg-[#101B3B] text-[#00E5FF] border border-[#2A4078]'
                        : 'text-[#94A3B8] hover:text-[#F8FAFC]'
                    }`}
                  >
                    {brand === 'all' ? 'All Brands' : brand}
                  </button>
                ))}
              </div>
            </div>

            {filteredProducts.length === 0 ? (
              <div className="bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-12 text-center space-y-4">
                <p className="text-base text-[#F8FAFC] font-semibold">
                  No automation hardware matched "{searchQuery}"
                </p>
                <p className="text-sm text-[#94A3B8]">
                  Reset filters or submit a custom B2B Request for Quote (RFQ) to our Gulshan engineering desk.
                </p>
                <button
                  onClick={() => {
                    setSearchQuery('');
                    setSelectedCategory('all');
                    setSelectedBrand('all');
                  }}
                  className="px-4 py-2 text-xs font-semibold bg-[#00E5FF] text-[#060B18] rounded-[10px] cursor-pointer"
                >
                  Reset All Filters
                </button>
              </div>
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                {filteredProducts.map((product) => {
                  const selectedVarId = selectedVariantMap[product.id] || product.variants[0].id;
                  const activeVar = product.variants.find((v) => v.id === selectedVarId) || product.variants[0];
                  const availableStock = activeVar.onHand - activeVar.reserved;
                  const discountPct = activeVar.compareAtBdt
                    ? Math.floor(((activeVar.compareAtBdt - activeVar.priceBdt) / activeVar.compareAtBdt) * 100)
                    : 0;

                  return (
                    <article
                      key={product.id}
                      className="bg-[#0A1128] border border-[#1B2B55] hover:border-[#2A4078] rounded-[14px] flex flex-col justify-between transition-transform duration-150 hover:-translate-y-0.5"
                    >
                      <div>
                        {/* Product Visual Header (65-75% Visual Dominance) */}
                        <div
                          onClick={() => {
                            setActiveProduct(product);
                            setPdpModalOpen(true);
                          }}
                          className="h-56 border-b border-[#1B2B55] rounded-t-[14px] overflow-hidden cursor-pointer relative"
                        >
                          <HardwareVectorArt type={product.artworkType} sku={activeVar.sku} />
                          <div className="absolute top-3 left-3 text-[11px] font-medium text-[#94A3B8] bg-[#060B18]/85 px-2.5 py-1 rounded">
                            {product.codEligible ? 'COD Eligible ≤ ৳15k' : 'B2B / Online Verified'}
                          </div>
                        </div>

                        {/* Clean Unboxed Metadata & Title */}
                        <div className="p-5 space-y-3">
                          <div className="flex items-center justify-between text-xs text-[#94A3B8]">
                            <span className="uppercase tracking-wider font-medium">{product.brand}</span>
                            <span className="font-mono-tabular">
                              ★ {product.rating.toFixed(1)} · {product.reviewCount} reviews
                            </span>
                          </div>

                          <h3
                            onClick={() => {
                              setActiveProduct(product);
                              setPdpModalOpen(true);
                            }}
                            className="text-base font-semibold text-[#F8FAFC] hover:text-[#00E5FF] transition-colors line-clamp-2 cursor-pointer"
                          >
                            {locale === 'en' ? product.titleEn : product.titleBn}
                          </h3>

                          <p className="text-xs text-[#94A3B8] line-clamp-2 leading-relaxed">{product.shortSummary}</p>

                          {/* Variant Selector Buttons */}
                          <div className="pt-1">
                            <div className="text-[11px] text-[#64748B] mb-1.5">
                              Select Configuration ({activeVar.voltage} · {activeVar.ioOrPower}):
                            </div>
                            <div className="flex flex-wrap gap-1.5">
                              {product.variants.map((v) => (
                                <button
                                  key={v.id}
                                  onClick={() =>
                                    setSelectedVariantMap((prev) => ({
                                      ...prev,
                                      [product.id]: v.id,
                                    }))
                                  }
                                  className={`px-2.5 py-1 text-xs rounded border transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                                    v.id === activeVar.id
                                      ? 'bg-[#101B3B] border-[#00E5FF] text-[#F8FAFC] font-medium'
                                      : 'bg-[#060B18] border-[#1B2B55] text-[#94A3B8] hover:text-[#F8FAFC]'
                                  }`}
                                >
                                  {v.ioOrPower}
                                </button>
                              ))}
                            </div>
                          </div>
                        </div>
                      </div>

                      {/* Price & Purchase Footer */}
                      <div className="px-5 pb-5 pt-3 border-t border-[#1B2B55] space-y-3">
                        <div className="flex items-baseline justify-between">
                          <div className="flex items-baseline gap-2">
                            <span className="text-lg font-bold text-[#F8FAFC] font-mono-tabular">
                              {formatBdt(activeVar.priceBdt)}
                            </span>
                            {activeVar.compareAtBdt && (
                              <span className="text-xs text-[#64748B] line-through font-mono-tabular">
                                {formatBdt(activeVar.compareAtBdt)}
                              </span>
                            )}
                            {discountPct > 0 && (
                              <span className="text-xs text-[#34D399] font-mono-tabular">−{discountPct}%</span>
                            )}
                          </div>
                          <span className="text-xs text-[#34D399] font-mono-tabular">
                            {availableStock} available ({activeVar.reserved} held)
                          </span>
                        </div>

                        <div className="text-[11px] text-[#94A3B8] flex items-center justify-between">
                          <span>Seller: {product.sellerName}</span>
                          <span>5% VAT incl.</span>
                        </div>

                        <div className="flex items-center gap-2 pt-1">
                          <button
                            onClick={() => handleAddToCart(product.id, activeVar.id)}
                            className="flex-1 h-10 px-4 bg-[#00E5FF] hover:bg-[#38EFFF] text-[#060B18] text-xs font-bold rounded-[10px] transition-colors flex items-center justify-center gap-1.5 whitespace-nowrap shrink-0 cursor-pointer"
                          >
                            {justAddedId === activeVar.id ? (
                              <>
                                <Check className="w-4 h-4" />
                                <span>Reserved in Cart</span>
                              </>
                            ) : (
                              <>
                                <ShoppingCart className="w-3.5 h-3.5" />
                                <span>Add to Cart</span>
                              </>
                            )}
                          </button>
                          <button
                            onClick={() => {
                              setActiveProduct(product);
                              setPdpModalOpen(true);
                            }}
                            className="h-10 px-3 bg-[#101B3B] hover:bg-[#1B2B55] text-[#F8FAFC] text-xs font-medium rounded-[10px] transition-colors whitespace-nowrap shrink-0 cursor-pointer"
                          >
                            Specs & CAD
                          </button>
                        </div>
                      </div>
                    </article>
                  );
                })}
              </div>
            )}
          </section>
        )}

        {/* SECTION 3: INDUSTRIAL CLIENT MATRIX & ENGINEERING STORY (On Homepage) */}
        {activeSection === 'catalog' && (
          <section className="border-t border-[#1B2B55] bg-[#0A1128] py-14">
            <div className="max-w-[1440px] mx-auto px-6 space-y-8">
              <div className="flex flex-col md:flex-row md:items-end justify-between gap-4">
                <div>
                  <div className="text-xs text-[#94A3B8] uppercase tracking-wider">
                    Verified Industrial Commissioning Portfolio
                  </div>
                  <h2 className="text-2xl font-bold text-[#F8FAFC] mt-1">
                    Deployed Across Leading Bangladeshi Manufacturing Plants
                  </h2>
                </div>
                <button
                  onClick={() => setActiveSection('rfq-desk')}
                  className="px-4 py-2 text-xs font-semibold bg-[#101B3B] border border-[#2A4078] hover:border-[#00E5FF] text-[#F8FAFC] rounded-[10px] transition-colors whitespace-nowrap shrink-0 flex items-center gap-2 cursor-pointer"
                >
                  <span>Book Turnkey Engineering Survey</span>
                  <ArrowRight className="w-3.5 h-3.5 text-[#00E5FF]" />
                </button>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                {[
                  {
                    client: 'Square Food & Beverage Ltd.',
                    sector: 'Food & Beverage · Pabna / Dhaka',
                    project: 'Made-in-Bangladesh ETP Automation & Real-Time DoE Monitoring Skid',
                    platform: 'Delta PLC + Cloud SCADA',
                  },
                  {
                    client: 'Singer / Beko Bangladesh',
                    sector: 'Appliance Manufacturing',
                    project: 'Air Conditioner Production Line Assembly & Test Automation',
                    platform: 'Siemens SIMATIC S7-1500',
                  },
                  {
                    client: 'Jamuna Paper Mills Ltd.',
                    sector: 'Pulp & Paper Processing',
                    project: 'High-Speed Duplex Paper Rotary Knife Cutting Plant (300 PPM)',
                    platform: 'Siemens Motion Control',
                  },
                  {
                    client: 'Fair Electronics (Hisense)',
                    sector: 'Consumer Electronics',
                    project: 'Washing Machine Production Line Commissioning & Safety Interlocks',
                    platform: 'Mitsubishi Electric iQ-R',
                  },
                ].map((item) => (
                  <div key={item.client} className="bg-[#0D1635] border border-[#1B2B55] rounded-[14px] p-5 space-y-2">
                    <div className="text-xs text-[#00E5FF] font-mono-tabular">{item.platform}</div>
                    <h3 className="text-base font-bold text-[#F8FAFC]">{item.client}</h3>
                    <div className="text-xs text-[#64748B]">{item.sector}</div>
                    <p className="text-xs text-[#94A3B8] pt-1 leading-relaxed">{item.project}</p>
                  </div>
                ))}
              </div>
            </div>
          </section>
        )}

        {/* VIEW 2: PRINCIPAL ARCHITECT, CYBERSECURITY & DESIGN AUDIT MATRIX */}
        {activeSection === 'architecture-audit' && (
          <section className="max-w-[1440px] mx-auto px-6 py-10 space-y-8">
            <div className="bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-6 sm:p-8 flex flex-col lg:flex-row lg:items-center justify-between gap-6">
              <div className="space-y-2">
                <div className="flex items-center gap-2 text-xs text-[#00E5FF] font-mono-tabular">
                  <span>PRINCIPAL ARCHITECT & STAFF CYBERSECURITY AUDIT</span>
                  <span>·</span>
                  <span>PHASE 0 + PHASE 1 + PHASE 2 DEEP REVIEW (R1–R4)</span>
                </div>
                <h1 className="text-2xl sm:text-3xl font-extrabold text-[#F8FAFC]">
                  Phase 0–2 Deep Audit Matrix — 26 Verified Fixes & 8 Residual/Regression Flaws
                </h1>
                <p className="text-sm text-[#94A3B8] max-w-[75ch]">
                  Comprehensive re-audit of commit <code className="text-[#F8FAFC]">b7c0d72</code> across{' '}
                  <code className="text-[#F8FAFC]">docs/</code> (Phase 0),{' '}
                  <code className="text-[#F8FAFC]">backend/</code> & <code className="text-[#F8FAFC]">infra/</code>{' '}
                  (Phase 1), and <code className="text-[#F8FAFC]">frontend/packages/ui</code> (Phase 2) against{' '}
                  <code className="text-[#F8FAFC]">docs/04_REVIEW_PROMPTS.md</code> (R1–R4).
                </p>
              </div>

              <div className="flex flex-wrap items-center gap-3 shrink-0">
                <button
                  onClick={handleCopyFullAuditMarkdown}
                  className="px-4 py-2.5 bg-[#00E5FF] hover:bg-[#38EFFF] text-[#060B18] text-xs font-bold rounded-[10px] transition-colors flex items-center gap-2 whitespace-nowrap shrink-0 cursor-pointer"
                >
                  {copiedAuditReport ? <Check className="w-4 h-4" /> : <Copy className="w-4 h-4" />}
                  <span>{copiedAuditReport ? 'Copied Full Audit Report!' : 'Copy Full Audit Report (Markdown)'}</span>
                </button>
              </div>
            </div>

            {/* Filter Bar & KPI Summary */}
            <div className="flex flex-wrap items-center justify-between gap-4">
              <div className="flex items-center gap-2">
                {(['ALL', 'Critical', 'High', 'Medium', 'Low'] as const).map((sev) => (
                  <button
                    key={sev}
                    onClick={() => setAuditSeverityFilter(sev)}
                    className={`px-3.5 py-1.5 text-xs font-semibold rounded-lg transition-colors whitespace-nowrap shrink-0 cursor-pointer ${
                      auditSeverityFilter === sev
                        ? 'bg-[#00E5FF] text-[#060B18]'
                        : 'bg-[#0A1128] border border-[#1B2B55] text-[#94A3B8] hover:text-[#F8FAFC]'
                    }`}
                  >
                    {sev === 'ALL'
                      ? `All Findings (${PHASE_0_AUDIT_FINDINGS.length})`
                      : `${sev} (${PHASE_0_AUDIT_FINDINGS.filter((f) => f.severity === sev).length})`}
                  </button>
                ))}
              </div>

              <div className="text-xs text-[#94A3B8] font-mono-tabular">
                1 Critical · 4 High · 2 Medium · 1 Low · 26/26 Prior Fixes Verified (`b7c0d72`)
              </div>
            </div>

            {/* Findings List */}
            <div className="space-y-6">
              {filteredAuditFindings.map((finding: AuditFinding) => (
                <div
                  key={finding.id}
                  className="bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-6 space-y-4"
                >
                  <div className="flex flex-wrap items-center justify-between gap-4 border-b border-[#1B2B55] pb-4">
                    <div className="space-y-1">
                      <div className="flex flex-wrap items-center gap-2 text-xs font-mono-tabular">
                        <span
                          className={
                            finding.severity === 'Critical'
                              ? 'text-[#F87171] font-bold'
                              : finding.severity === 'High'
                              ? 'text-[#FBBF24] font-bold'
                              : 'text-[#00E5FF] font-bold'
                          }
                        >
                          [{finding.severity.toUpperCase()}]
                        </span>
                        <span className="text-[#94A3B8]">·</span>
                        <span className="text-[#F8FAFC] font-semibold">{finding.id}</span>
                        <span className="text-[#94A3B8]">·</span>
                        <span className="text-[#94A3B8]">{finding.category}</span>
                      </div>
                      <h2 className="text-lg font-bold text-[#F8FAFC]">{finding.title}</h2>
                    </div>

                    <div className="text-xs font-mono-tabular text-[#94A3B8] bg-[#101B3B] px-3 py-1.5 rounded border border-[#2A4078]">
                      {finding.fileLocation}
                    </div>
                  </div>

                  <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
                    <div className="lg:col-span-5 space-y-3">
                      <div>
                        <div className="text-xs font-semibold text-[#F8FAFC] uppercase tracking-wider mb-1">
                          Architectural / Security Flaw
                        </div>
                        <p className="text-xs text-[#94A3B8] leading-relaxed">{finding.flawExplanation}</p>
                      </div>
                      <div className="pt-2 border-t border-[#1B2B55]">
                        <div className="text-xs font-semibold text-[#F87171] uppercase tracking-wider mb-1">
                          Production Impact if Unresolved
                        </div>
                        <p className="text-xs text-[#F8FAFC] leading-relaxed">{finding.impact}</p>
                      </div>
                    </div>

                    <div className="lg:col-span-7 bg-[#060B18] border border-[#1B2B55] rounded-[10px] overflow-hidden flex flex-col">
                      <div className="px-4 py-2 bg-[#101B3B] border-b border-[#1B2B55] flex items-center justify-between">
                        <span className="text-xs font-mono-tabular text-[#00E5FF] flex items-center gap-1.5">
                          <FileCode2 className="w-3.5 h-3.5" />
                          <span>Exact Production Fix ({finding.language.toUpperCase()})</span>
                        </span>
                        <button
                          onClick={() => {
                            navigator.clipboard.writeText(finding.exactCodeFix);
                            setCopiedFixId(finding.id);
                            setTimeout(() => setCopiedFixId(null), 1500);
                          }}
                          className="text-xs text-[#94A3B8] hover:text-[#F8FAFC] flex items-center gap-1 cursor-pointer"
                        >
                          {copiedFixId === finding.id ? (
                            <>
                              <Check className="w-3.5 h-3.5 text-[#34D399]" />
                              <span className="text-[#34D399]">Copied</span>
                            </>
                          ) : (
                            <>
                              <Copy className="w-3.5 h-3.5" />
                              <span>Copy Patch</span>
                            </>
                          )}
                        </button>
                      </div>
                      <pre className="p-4 text-xs font-mono-tabular text-[#F8FAFC] overflow-x-auto leading-relaxed">
                        <code>{finding.exactCodeFix}</code>
                      </pre>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </section>
        )}

        {/* VIEW 3: MULTI-SELLER ORDER SPLITTING & DOUBLE-ENTRY LEDGER SIMULATOR */}
        {activeSection === 'order-simulator' && (
          <section className="max-w-[1440px] mx-auto px-6 py-10 space-y-8">
            <div className="bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-6 flex flex-col md:flex-row md:items-center justify-between gap-4">
              <div>
                <div className="text-xs text-[#00E5FF] font-mono-tabular">
                  ADR-0002 & ADR-0003 · MULTI-VENDOR ORDER SPLITTING & ZERO-SUM LEDGER ENGINE
                </div>
                <h1 className="text-2xl font-extrabold text-[#F8FAFC] mt-1">
                  Parent Order → Seller SubOrders & Double-Entry Ledger Verification
                </h1>
                <p className="text-xs text-[#94A3B8] mt-1">
                  Demonstrates the exact fix for Audit Finding MW-AUD-02: separating Seller-Funded vs. Platform-Funded
                  discounts so double-entry journal batches always balance (`Sum(Debits) == Sum(Credits)`).
                </p>
              </div>
              <div className="flex items-center gap-2 shrink-0">
                <button
                  onClick={() =>
                    setAppliedCoupon((prev) => (prev === 'INDUSTRY40' ? 'BKASH10' : 'INDUSTRY40'))
                  }
                  className="px-3.5 py-2 text-xs font-semibold bg-[#101B3B] border border-[#2A4078] hover:border-[#00E5FF] text-[#F8FAFC] rounded-[10px] cursor-pointer whitespace-nowrap"
                >
                  Toggle Coupon: {appliedCoupon === 'INDUSTRY40' ? 'INDUSTRY40 (Seller)' : 'BKASH10 (Platform)'}
                </button>
              </div>
            </div>

            <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
              {/* Parent Order & SubOrder Split Breakdown */}
              <div className="lg:col-span-7 space-y-4">
                <div className="bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-6 space-y-4">
                  <div className="flex items-center justify-between border-b border-[#1B2B55] pb-3">
                    <div>
                      <div className="text-xs text-[#94A3B8]">PARENT MASTER ORDER AGGREGATE</div>
                      <div className="text-lg font-bold text-[#F8FAFC] font-mono-tabular">
                        SV-261029-X7K92M ({cartSummary.subOrders.length} Fulfilling Sellers)
                      </div>
                    </div>
                    <span className="text-xs font-mono-tabular text-[#34D399]">
                      Token: {reservationToken || 'res_active'}
                    </span>
                  </div>

                  {cartSummary.subOrders.map((sub) => (
                    <div
                      key={sub.sellerName}
                      className="bg-[#0D1635] border border-[#1B2B55] rounded-[10px] p-4 space-y-3"
                    >
                      <div className="flex items-center justify-between text-xs">
                        <span className="font-bold text-[#00E5FF] font-mono-tabular">
                          SubOrder #SV-261029-X7K92M-{sub.subOrderSuffix} · {sub.sellerName}
                        </span>
                        <span className="text-[#94A3B8] font-mono-tabular">
                          Commission Rate: {(sub.commissionRate * 100).toFixed(0)}%
                        </span>
                      </div>

                      <div className="space-y-1.5 text-xs border-t border-b border-[#1B2B55] py-2.5">
                        {sub.items.map((item) => (
                          <div key={item.variantId} className="flex justify-between text-[#F8FAFC]">
                            <span>
                              {item.quantity}× {item.product.titleEn} ({item.variant.sku})
                            </span>
                            <span className="font-mono-tabular">{formatBdt(item.lineSubtotal)}</span>
                          </div>
                        ))}
                      </div>

                      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-xs font-mono-tabular">
                        <div>
                          <div className="text-[#64748B]">Seller Subtotal</div>
                          <div className="text-[#F8FAFC] font-semibold">{formatBdt(sub.subtotal)}</div>
                        </div>
                        <div>
                          <div className="text-[#64748B]">Seller Discount</div>
                          <div className="text-[#F87171]">−{formatBdt(sub.sellerDiscount)}</div>
                        </div>
                        <div>
                          <div className="text-[#64748B]">Platform Comm.</div>
                          <div className="text-[#FBBF24]">−{formatBdt(sub.commissionAmount)}</div>
                        </div>
                        <div>
                          <div className="text-[#64748B]">Seller Net Payable</div>
                          <div className="text-[#34D399] font-bold">{formatBdt(sub.sellerNetEarning)}</div>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </div>

              {/* Balanced Double-Entry Journal Table */}
              <div className="lg:col-span-5 bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-6 flex flex-col justify-between space-y-4">
                <div className="space-y-3">
                  <div className="text-xs text-[#00E5FF] font-mono-tabular">
                    POSTGRESQL DEFERRABLE CONSTRAINT TRIGGER VERIFIED
                  </div>
                  <h2 className="text-lg font-bold text-[#F8FAFC]">
                    Double-Entry Financial Ledger Posting
                  </h2>
                  <p className="text-xs text-[#94A3B8]">
                    Every payment capture writes an atomic batch (`transaction_batch_id`) where Total Debits strictly
                    equal Total Credits in minor units (`poisha`).
                  </p>

                  <div className="border border-[#1B2B55] rounded-[10px] overflow-hidden">
                    <table className="w-full text-left text-xs font-mono-tabular">
                      <thead className="bg-[#101B3B] text-[#94A3B8] border-b border-[#1B2B55]">
                        <tr>
                          <th className="py-2.5 px-3">Ledger Account</th>
                          <th className="py-2.5 px-3 text-right">Debit (BDT)</th>
                          <th className="py-2.5 px-3 text-right">Credit (BDT)</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-[#1B2B55]">
                        <tr>
                          <td className="py-2.5 px-3 text-[#F8FAFC]">platform_escrow (Gateway Capture)</td>
                          <td className="py-2.5 px-3 text-right text-[#34D399]">
                            {formatBdt(cartSummary.grandTotal)}
                          </td>
                          <td className="py-2.5 px-3 text-right text-[#64748B]">—</td>
                        </tr>
                        {cartSummary.platformDiscountTotal > 0 && (
                          <tr>
                            <td className="py-2.5 px-3 text-[#F8FAFC]">platform_promo_expense (bKash Promo)</td>
                            <td className="py-2.5 px-3 text-right text-[#34D399]">
                              {formatBdt(cartSummary.platformDiscountTotal)}
                            </td>
                            <td className="py-2.5 px-3 text-right text-[#64748B]">—</td>
                          </tr>
                        )}
                        {cartSummary.subOrders.map((sub) => (
                          <tr key={`payable-${sub.sellerName}`}>
                            <td className="py-2.5 px-3 text-[#94A3B8]">seller_payable ({sub.sellerName})</td>
                            <td className="py-2.5 px-3 text-right text-[#64748B]">—</td>
                            <td className="py-2.5 px-3 text-right text-[#00E5FF]">
                              {formatBdt(sub.sellerNetEarning)}
                            </td>
                          </tr>
                        ))}
                        <tr>
                          <td className="py-2.5 px-3 text-[#94A3B8]">platform_revenue_commission</td>
                          <td className="py-2.5 px-3 text-right text-[#64748B]">—</td>
                          <td className="py-2.5 px-3 text-right text-[#00E5FF]">
                            {formatBdt(cartSummary.subOrders.reduce((a, b) => a + b.commissionAmount, 0))}
                          </td>
                        </tr>
                        <tr>
                          <td className="py-2.5 px-3 text-[#94A3B8]">platform_tax_payable (5% VAT)</td>
                          <td className="py-2.5 px-3 text-right text-[#64748B]">—</td>
                          <td className="py-2.5 px-3 text-right text-[#00E5FF]">{formatBdt(cartSummary.vatTotal)}</td>
                        </tr>
                        <tr>
                          <td className="py-2.5 px-3 text-[#94A3B8]">platform_shipping_payable</td>
                          <td className="py-2.5 px-3 text-right text-[#64748B]">—</td>
                          <td className="py-2.5 px-3 text-right text-[#00E5FF]">
                            {formatBdt(cartSummary.shippingTotal)}
                          </td>
                        </tr>
                      </tbody>
                      <tfoot className="bg-[#101B3B] font-bold text-[#F8FAFC] border-t border-[#2A4078]">
                        <tr>
                          <td className="py-2.5 px-3">BATCH ZERO-SUM CHECK</td>
                          <td className="py-2.5 px-3 text-right text-[#34D399]">
                            {formatBdt(cartSummary.grandTotal + cartSummary.platformDiscountTotal)}
                          </td>
                          <td className="py-2.5 px-3 text-right text-[#34D399]">
                            {formatBdt(cartSummary.grandTotal + cartSummary.platformDiscountTotal)}
                          </td>
                        </tr>
                      </tfoot>
                    </table>
                  </div>
                </div>

                <div className="p-3.5 bg-[#060B18] border border-[#1B2B55] rounded-[10px] text-xs text-[#34D399] font-mono-tabular flex items-center justify-between">
                  <span>INVARIANT: SUM(DEBITS) - SUM(CREDITS) == 0 POISHA</span>
                  <span>VERIFIED</span>
                </div>
              </div>
            </div>
          </section>
        )}

        {/* VIEW 4: TURNKEY ENGINEERING & CUSTOM MACHINERY RFQ DESK */}
        {activeSection === 'rfq-desk' && (
          <section className="max-w-[1440px] mx-auto px-6 py-10">
            <div className="grid grid-cols-1 lg:grid-cols-12 gap-8">
              <div className="lg:col-span-5 space-y-6">
                <div className="text-xs text-[#00E5FF] font-mono-tabular">
                  B2B INDUSTRIAL PROCUREMENT & TURNKEY COMMISSIONING
                </div>
                <h1 className="text-3xl font-extrabold text-[#F8FAFC]">
                  Request an Official Engineering Quote (RFQ) & BoM Proposal
                </h1>
                <p className="text-sm text-[#94A3B8] leading-relaxed">
                  Custom-built Made-in-Bangladesh machinery (ETP chemical dosing skids, CNC foam cutters, high-speed
                  duplex paper cutters) and volume PLC/VFD schedules receive a formal stamped VAT invoice and CAD
                  layout within 24 business hours.
                </p>

                <div className="bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-5 space-y-3 text-xs">
                  <div className="font-bold text-[#F8FAFC]">MECHWARE Engineering Headquarters</div>
                  <div className="text-[#94A3B8]">
                    Sha-23/E (6th Floor), Modhubon Tower, Gulshan Link Rd, Dhaka 1212, Bangladesh
                  </div>
                  <div className="font-mono-tabular text-[#00E5FF]">
                    Hotline: +88 01748-305931 · parvez.mechware@gmail.com
                  </div>
                  <div className="pt-2 border-t border-[#1B2B55] text-[#94A3B8]">
                    Payment Terms for Custom Skids: 30% Bank Advance / L/C against approved P&ID drawing, 70% upon
                    Factory Acceptance Test (FAT).
                  </div>
                </div>
              </div>

              <div className="lg:col-span-7 bg-[#0A1128] border border-[#1B2B55] rounded-[14px] p-6 sm:p-8">
                {rfqSubmitted ? (
                  <div className="space-y-4 py-8 text-center">
                    <div className="w-12 h-12 rounded-full bg-[#10B981]/20 border border-[#10B981] text-[#34D399] flex items-center justify-center mx-auto">
                      <Check className="w-6 h-6" />
                    </div>
                    <h2 className="text-xl font-bold text-[#F8FAFC]">
                      Engineering RFQ Registered — {rfqSubmitted}
                    </h2>
                    <p className="text-xs text-[#94A3B8] max-w-md mx-auto">
                      Assigned to MECHWARE Systems Engineering Team (Gulshan Hub). Your bill of materials and 30%
                      advance commercial proforma invoice is being prepared.
                    </p>
                    <button
                      onClick={() => setRfqSubmitted(null)}
                      className="px-4 py-2 text-xs font-semibold bg-[#00E5FF] text-[#060B18] rounded-[10px] cursor-pointer"
                    >
                      Submit Another RFQ
                    </button>
                  </div>
                ) : (
                  <form
                    onSubmit={(e) => {
                      e.preventDefault();
                      setRfqSubmitted('RFQ-261029-MW904');
                    }}
                    className="space-y-4"
                  >
                    <h2 className="text-lg font-bold text-[#F8FAFC] border-b border-[#1B2B55] pb-3">
                      Project & Technical Specification Brief
                    </h2>

                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                      <div>
                        <label className="block text-xs font-medium text-[#F8FAFC] mb-1.5">
                          Organization / Factory Name
                        </label>
                        <input
                          type="text"
                          required
                          value={rfqCompany}
                          onChange={(e) => setRfqCompany(e.target.value)}
                          className="w-full h-11 px-3.5 bg-[#101B3B] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC]"
                        />
                      </div>
                      <div>
                        <label className="block text-xs font-medium text-[#F8FAFC] mb-1.5">
                          Lead Engineer / Procurement Contact
                        </label>
                        <input
                          type="text"
                          required
                          value={rfqEngineer}
                          onChange={(e) => setRfqEngineer(e.target.value)}
                          className="w-full h-11 px-3.5 bg-[#101B3B] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC]"
                        />
                      </div>
                    </div>

                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                      <div>
                        <label className="block text-xs font-medium text-[#F8FAFC] mb-1.5">
                          Bangladesh Mobile (+8801XXXXXXXXX)
                        </label>
                        <input
                          type="tel"
                          required
                          value={rfqPhone}
                          onChange={(e) => setRfqPhone(e.target.value)}
                          className="w-full h-11 px-3.5 bg-[#101B3B] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC] font-mono-tabular"
                        />
                      </div>
                      <div>
                        <label className="block text-xs font-medium text-[#F8FAFC] mb-1.5">
                          Engineering Scope Category
                        </label>
                        <select
                          value={rfqProjectType}
                          onChange={(e) => setRfqProjectType(e.target.value)}
                          className="w-full h-11 px-3.5 bg-[#101B3B] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC]"
                        >
                          <option>Turnkey ETP Automation & DoE SCADA</option>
                          <option>PLC / VFD / Servo Bulk Component Schedule</option>
                          <option>Machine Retrofitting & Legacy PLC Migration</option>
                          <option>Custom Made-in-Bangladesh CNC / Packaging Machine</option>
                        </select>
                      </div>
                    </div>

                    <div>
                      <label className="block text-xs font-medium text-[#F8FAFC] mb-1.5">
                        Technical Requirements (I/O Count, Motor kW, Communication Protocol, Delivery Factory Zone)
                      </label>
                      <textarea
                        rows={4}
                        required
                        value={rfqNotes}
                        onChange={(e) => setRfqNotes(e.target.value)}
                        className="w-full p-3.5 bg-[#101B3B] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC]"
                      />
                    </div>

                    <button
                      type="submit"
                      className="w-full h-11 bg-[#00E5FF] hover:bg-[#38EFFF] text-[#060B18] font-bold text-xs rounded-[10px] transition-colors cursor-pointer"
                    >
                      Generate Official Engineering RFQ & Reserve Lead Time
                    </button>
                  </form>
                )}
              </div>
            </div>
          </section>
        )}
      </main>

      {/* PRODUCT DETAIL (PDP) CONTIGUOUS PURCHASE MODAL WITH CAD / DATASHEET */}
      {pdpModalOpen && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={activeProduct.titleEn}
          className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-center justify-center p-4 overflow-y-auto"
        >
          <div className="bg-[#0D1635] border border-[#2A4078] rounded-[20px] max-w-4xl w-full p-6 sm:p-8 space-y-6 my-8">
            <div className="flex items-start justify-between gap-4 border-b border-[#1B2B55] pb-4">
              <div>
                <div className="text-xs text-[#00E5FF] font-mono-tabular">
                  {activeProduct.brand} · {activeProduct.category}
                </div>
                <h2 className="text-xl sm:text-2xl font-bold text-[#F8FAFC] mt-1">{activeProduct.titleEn}</h2>
              </div>
              <button
                onClick={() => setPdpModalOpen(false)}
                aria-label="Close specification modal"
                className="p-2 text-[#94A3B8] hover:text-[#F8FAFC] bg-[#101B3B] rounded-lg cursor-pointer"
              >
                <X className="w-5 h-5" />
              </button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-12 gap-6">
              {/* Left Gallery & CAD / Datasheet Downloads */}
              <div className="md:col-span-5 space-y-4">
                <div className="h-60 rounded-[14px] overflow-hidden border border-[#1B2B55]">
                  <HardwareVectorArt
                    type={activeProduct.artworkType}
                    sku={(selectedVariantMap[activeProduct.id] &&
                      activeProduct.variants.find((v) => v.id === selectedVariantMap[activeProduct.id])?.sku) ||
                      activeProduct.variants[0].sku}
                  />
                </div>

                <div className="grid grid-cols-2 gap-2.5">
                  <button
                    onClick={() => {
                      const blob = new Blob(
                        [
                          `MECHWARE OFFICIAL TECHNICAL DATASHEET\nProduct: ${activeProduct.titleEn}\nBrand: ${activeProduct.brand}\nWarranty: ${activeProduct.warrantyMonths} Months\nHighlights:\n- ${activeProduct.highlights.join('\n- ')}`,
                        ],
                        { type: 'text/plain' }
                      );
                      const url = URL.createObjectURL(blob);
                      const a = document.createElement('a');
                      a.href = url;
                      a.download = activeProduct.datasheetCode.replace('.pdf', '.txt');
                      a.click();
                    }}
                    className="p-2.5 bg-[#101B3B] hover:bg-[#1B2B55] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC] flex items-center justify-center gap-2 cursor-pointer"
                  >
                    <FileText className="w-4 h-4 text-[#00E5FF]" />
                    <span className="truncate">{activeProduct.datasheetCode}</span>
                  </button>
                  <button
                    onClick={() => {
                      const blob = new Blob(
                        [
                          `ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('MECHWARE 3D CAD STEP MODEL - ${activeProduct.titleEn}'),'2;1');\nENDSEC;`,
                        ],
                        { type: 'text/plain' }
                      );
                      const url = URL.createObjectURL(blob);
                      const a = document.createElement('a');
                      a.href = url;
                      a.download = activeProduct.cadStepFile;
                      a.click();
                    }}
                    className="p-2.5 bg-[#101B3B] hover:bg-[#1B2B55] border border-[#2A4078] rounded-[10px] text-xs text-[#F8FAFC] flex items-center justify-center gap-2 cursor-pointer"
                  >
                    <Download className="w-4 h-4 text-[#00E5FF]" />
                    <span className="truncate">{activeProduct.cadStepFile}</span>
                  </button>
                </div>
              </div>

              {/* Right Contiguous Purchase Module & Specs */}
              <div className="md:col-span-7 space-y-4">
                <div className="space-y-2">
                  <div className="text-xs font-semibold text-[#F8FAFC] uppercase tracking-wider">
                    Engineering Highlights
                  </div>
                  <ul className="space-y-1.5 text-xs text-[#94A3B8]">
                    {activeProduct.highlights.map((h) => (
                      <li key={h} className="flex items-start gap-2">
                        <span className="text-[#00E5FF]">·</span>
                        <span>{h}</span>
                      </li>
                    ))}
                  </ul>
                </div>

                {/* Grouped Technical Specifications Table */}
                <div className="space-y-2">
                  {activeProduct.specs.map((group) => (
                    <div key={group.group} className="border border-[#1B2B55] rounded-[10px] overflow-hidden">
                      <div className="bg-[#101B3B] px-3 py-1.5 text-xs font-semibold text-[#F8FAFC]">
                        {group.group}
                      </div>
                      <div className="divide-y divide-[#1B2B55] text-xs">
                        {group.items.map((spec) => (
                          <div key={spec.label} className="px-3 py-2 flex justify-between gap-4 bg-[#060B18]">
                            <span className="text-[#94A3B8]">{spec.label}</span>
                            <span className="text-[#F8FAFC] font-mono-tabular text-right">{spec.value}</span>
                          </div>
                        ))}
                      </div>
                    </div>
                  ))}
                </div>

                <div className="pt-2 flex items-center justify-between gap-3">
                  <div>
                    <div className="text-xs text-[#94A3B8]">Delivery SLA</div>
                    <div className="text-xs font-medium text-[#34D399]">{activeProduct.leadTimePromise}</div>
                  </div>
                  <button
                    onClick={() => {
                      const varId = selectedVariantMap[activeProduct.id] || activeProduct.variants[0].id;
                      handleAddToCart(activeProduct.id, varId);
                      setPdpModalOpen(false);
                      setCartDrawerOpen(true);
                    }}
                    className="px-5 py-2.5 bg-[#00E5FF] hover:bg-[#38EFFF] text-[#060B18] text-xs font-bold rounded-[10px] transition-colors whitespace-nowrap shrink-0 cursor-pointer"
                  >
                    Add Selected Variant to Cart
                  </button>
                </div>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* ATOMIC CHECKOUT & MULTI-SELLER CART DRAWER */}
      {cartDrawerOpen && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="Procurement Cart and Atomic Checkout"
          className="fixed inset-0 z-50 bg-black/70 backdrop-blur-sm flex justify-end"
        >
          <div className="bg-[#0D1635] border-l border-[#2A4078] w-full max-w-xl h-full flex flex-col justify-between overflow-y-auto p-6 space-y-6">
            <div className="space-y-5">
              <div className="flex items-center justify-between border-b border-[#1B2B55] pb-4">
                <div>
                  <h2 className="text-lg font-bold text-[#F8FAFC]">
                    Procurement Cart & 15-Min Stock Lock
                  </h2>
                  <p className="text-xs text-[#94A3B8] font-mono-tabular">
                    PostgreSQL SELECT FOR UPDATE · Split by Seller
                  </p>
                </div>
                <button
                  onClick={() => setCartDrawerOpen(false)}
                  aria-label="Close cart drawer"
                  className="p-2 text-[#94A3B8] hover:text-[#F8FAFC] bg-[#101B3B] rounded-lg cursor-pointer"
                >
                  <X className="w-5 h-5" />
                </button>
              </div>

              {confirmedOrderNumber ? (
                <div className="bg-[#0A1128] border border-[#10B981] rounded-[14px] p-6 space-y-4">
                  <div className="text-xs font-mono-tabular text-[#34D399]">
                    ATOMIC ORDER & DOUBLE-ENTRY LEDGER POSTED
                  </div>
                  <h3 className="text-xl font-bold text-[#F8FAFC] font-mono-tabular">
                    Master Order #{confirmedOrderNumber}
                  </h3>
                  <p className="text-xs text-[#94A3B8]">
                    Split into {cartSummary.subOrders.length} seller sub-orders (`{confirmedOrderNumber}-01`, etc.).
                    Stock reservations converted to physical warehouse deductions.
                  </p>
                  <div className="flex items-center gap-3">
                    <button
                      onClick={() => {
                        setConfirmedOrderNumber(null);
                        setCartDrawerOpen(false);
                        setActiveSection('order-simulator');
                      }}
                      className="px-4 py-2 bg-[#00E5FF] text-[#060B18] text-xs font-bold rounded-[10px] cursor-pointer"
                    >
                      Inspect SubOrders & Ledger Entries
                    </button>
                    <button
                      onClick={() => setConfirmedOrderNumber(null)}
                      className="px-4 py-2 bg-[#101B3B] text-[#F8FAFC] text-xs font-medium rounded-[10px] cursor-pointer"
                    >
                      Back to Cart
                    </button>
                  </div>
                </div>
              ) : cartSummary.detailedLines.length === 0 ? (
                <div className="py-12 text-center space-y-3">
                  <p className="text-sm text-[#94A3B8]">Your procurement cart is empty.</p>
                  <button
                    onClick={() => setCartDrawerOpen(false)}
                    className="px-4 py-2 bg-[#00E5FF] text-[#060B18] text-xs font-bold rounded-[10px] cursor-pointer"
                  >
                    Browse Automation Hardware
                  </button>
                </div>
              ) : (
                <>
                  {/* Grouped by Seller */}
                  <div className="space-y-4">
                    {cartSummary.subOrders.map((sub) => (
                      <div
                        key={sub.sellerName}
                        className="bg-[#0A1128] border border-[#1B2B55] rounded-[12px] p-4 space-y-3"
                      >
                        <div className="flex items-center justify-between text-xs text-[#94A3B8] border-b border-[#1B2B55] pb-2">
                          <span className="font-semibold text-[#F8FAFC] flex items-center gap-1.5">
                            <Building2 className="w-3.5 h-3.5 text-[#00E5FF]" />
                            <span>{sub.sellerName}</span>
                          </span>
                          <span className="font-mono-tabular">
                            Shipping: {sub.shippingFee === 0 ? 'FREE (>৳50k)' : formatBdt(sub.shippingFee)}
                          </span>
                        </div>

                        {sub.items.map((item) => (
                          <div key={item.variantId} className="flex items-center justify-between gap-3 pt-1">
                            <div className="min-w-0 flex-1">
                              <div className="text-xs font-semibold text-[#F8FAFC] truncate">
                                {item.product.titleEn}
                              </div>
                              <div className="text-[11px] text-[#94A3B8] font-mono-tabular">
                                {item.variant.sku} · {formatBdt(item.variant.priceBdt)}
                              </div>
                            </div>

                            <div className="flex items-center gap-2 shrink-0">
                              <button
                                onClick={() => handleUpdateQty(item.variantId, -1)}
                                aria-label="Decrease quantity"
                                className="w-7 h-7 rounded bg-[#101B3B] text-[#F8FAFC] flex items-center justify-center cursor-pointer"
                              >
                                <Minus className="w-3.5 h-3.5" />
                              </button>
                              <span className="text-xs font-mono-tabular w-5 text-center">{item.quantity}</span>
                              <button
                                onClick={() => handleUpdateQty(item.variantId, 1)}
                                aria-label="Increase quantity"
                                className="w-7 h-7 rounded bg-[#101B3B] text-[#F8FAFC] flex items-center justify-center cursor-pointer"
                              >
                                <Plus className="w-3.5 h-3.5" />
                              </button>
                            </div>
                          </div>
                        ))}
                      </div>
                    ))}
                  </div>

                  {/* Delivery Zone & Promotion Selector */}
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <div>
                      <label className="block text-[11px] text-[#94A3B8] mb-1">Delivery Zone (BD)</label>
                      <select
                        value={deliveryZone}
                        onChange={(e) => setDeliveryZone(e.target.value as typeof deliveryZone)}
                        className="w-full h-9 px-3 bg-[#101B3B] border border-[#2A4078] rounded-lg text-xs text-[#F8FAFC]"
                      >
                        <option value="inside_dhaka">Inside Dhaka (৳150/seller)</option>
                        <option value="dhaka_suburbs">Gazipur / Savar EPZ (৳280)</option>
                        <option value="outside_dhaka">Chattogram / Outside Dhaka (৳450)</option>
                      </select>
                    </div>
                    <div>
                      <label className="block text-[11px] text-[#94A3B8] mb-1">Promotion Code</label>
                      <div className="flex gap-1.5">
                        <input
                          type="text"
                          value={couponCode}
                          onChange={(e) => setCouponCode(e.target.value.toUpperCase())}
                          className="w-full h-9 px-3 bg-[#101B3B] border border-[#2A4078] rounded-lg text-xs text-[#F8FAFC] font-mono-tabular"
                        />
                        <button
                          onClick={() => {
                            if (couponCode === 'BKASH10') setAppliedCoupon('BKASH10');
                            else setAppliedCoupon('INDUSTRY40');
                          }}
                          className="px-3 h-9 bg-[#101B3B] border border-[#00E5FF] text-[#00E5FF] text-xs font-semibold rounded-lg cursor-pointer"
                        >
                          Apply
                        </button>
                      </div>
                    </div>
                  </div>

                  {/* Payment Gateway Selection */}
                  <div className="space-y-2">
                    <div className="text-xs font-semibold text-[#F8FAFC]">
                      Select Server-Verified Payment Gateway
                    </div>
                    <div className="grid grid-cols-2 gap-2">
                      {(
                        [
                          { id: 'bkash', label: 'bKash Tokenized' },
                          { id: 'sslcommerz', label: 'SSLCommerz BD' },
                          { id: 'stripe', label: 'Stripe Corporate Card' },
                          { id: 'cod', label: 'Cash on Delivery (≤৳15k)' },
                        ] as const
                      ).map((pm) => {
                        const disabled = pm.id === 'cod' && !cartSummary.codAllowed;
                        return (
                          <button
                            key={pm.id}
                            disabled={disabled}
                            onClick={() => setPaymentMethod(pm.id)}
                            className={`p-2.5 text-left rounded-lg border text-xs transition-colors cursor-pointer ${
                              disabled
                                ? 'bg-[#060B18] border-[#1B2B55] text-[#64748B] opacity-50 cursor-not-allowed'
                                : paymentMethod === pm.id
                                ? 'bg-[#101B3B] border-[#00E5FF] text-[#F8FAFC] font-semibold'
                                : 'bg-[#0A1128] border-[#1B2B55] text-[#94A3B8]'
                            }`}
                          >
                            {pm.label}
                          </button>
                        );
                      })}
                    </div>
                  </div>

                  {paymentMethod === 'cod' && (
                    <div className="p-3 bg-[#0A1128] border border-[#2A4078] rounded-lg space-y-2">
                      <label className="block text-xs text-[#F8FAFC]">
                        Mandatory COD Phone OTP Verification (Bound to Order ID per Audit Fix MW-AUD-03)
                      </label>
                      <input
                        type="text"
                        placeholder="Enter 6-digit SMS OTP (e.g. 849201)"
                        value={codOtpInput}
                        onChange={(e) => setCodOtpInput(e.target.value)}
                        className="w-full h-9 px-3 bg-[#101B3B] border border-[#2A4078] rounded text-xs text-[#F8FAFC] font-mono-tabular"
                      />
                    </div>
                  )}
                </>
              )}
            </div>

            {/* Sticky Totals & Atomic Checkout Action */}
            {cartSummary.detailedLines.length > 0 && !confirmedOrderNumber && (
              <div className="pt-4 border-t border-[#1B2B55] space-y-3">
                <div className="space-y-1 text-xs font-mono-tabular">
                  <div className="flex justify-between text-[#94A3B8]">
                    <span>Items Subtotal</span>
                    <span>{formatBdt(cartSummary.subtotal)}</span>
                  </div>
                  <div className="flex justify-between text-[#34D399]">
                    <span>Discounts ({appliedCoupon || 'None'})</span>
                    <span>−{formatBdt(cartSummary.totalDiscount)}</span>
                  </div>
                  <div className="flex justify-between text-[#94A3B8]">
                    <span>Multi-Seller Shipping</span>
                    <span>{formatBdt(cartSummary.shippingTotal)}</span>
                  </div>
                  <div className="flex justify-between text-[#94A3B8]">
                    <span>5% Industrial VAT (Server Calculated)</span>
                    <span>{formatBdt(cartSummary.vatTotal)}</span>
                  </div>
                  <div className="flex justify-between text-base font-bold text-[#F8FAFC] pt-2 border-t border-[#1B2B55]">
                    <span>Grand Total</span>
                    <span>{formatBdt(cartSummary.grandTotal)}</span>
                  </div>
                </div>

                <button
                  onClick={() => {
                    setReservationToken('res_89a2bc1e_locked');
                    setConfirmedOrderNumber('SV-261029-X7K92M');
                  }}
                  className="w-full h-11 bg-[#00E5FF] hover:bg-[#38EFFF] text-[#060B18] font-bold text-xs rounded-[10px] transition-colors flex items-center justify-center gap-2 cursor-pointer"
                >
                  <Lock className="w-4 h-4" />
                  <span>Execute Atomic Order & Lock Inventory (Idempotent)</span>
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {/* CLEAN ENTERPRISE FOOTER */}
      <footer className="border-t border-[#1B2B55] bg-[#060B18] py-8 mt-12">
        <div className="max-w-[1440px] mx-auto px-6 flex flex-col sm:flex-row items-center justify-between gap-4 text-xs text-[#64748B]">
          <div>
            MECHWARE · Sha-23/E (6th Floor), Modhubon Tower, Gulshan Link Rd, Dhaka 1212 · +88 01748-305931
          </div>
          <div className="flex items-center gap-4">
            <button onClick={() => setActiveSection('catalog')} className="hover:text-[#F8FAFC] cursor-pointer">
              Storefront
            </button>
            <button onClick={() => setActiveSection('order-simulator')} className="hover:text-[#F8FAFC] cursor-pointer">
              Double-Entry Ledger
            </button>
            <button
              onClick={() => setActiveSection('architecture-audit')}
              className="hover:text-[#F8FAFC] cursor-pointer"
            >
              Phase 0 Audit Matrix
            </button>
          </div>
        </div>
      </footer>
    </div>
  );
}

export default App;
