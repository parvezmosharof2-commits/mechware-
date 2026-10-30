namespace ShopVerse.Infrastructure.Seed;

/// <summary>
/// Text banks and composition rules for synthetic-but-plausible reviews and Q&amp;A. Reviews mix technical remarks,
/// delivery/service comments, and a few Bangla / Banglish entries; ratings steer tone.
/// </summary>
internal static class ReviewCorpus
{
    public static readonly Dictionary<string, string> RootDomain = new()
    {
        ["plcs-controllers"] = "plc",
        ["hmi-industrial-pcs"] = "hmi",
        ["drives-motion-control"] = "drive",
        ["sensors-switches"] = "sensor",
        ["motors-gearboxes"] = "motor",
        ["power-distribution-protection"] = "power",
        ["pneumatics-fluid-power"] = "pneumatic",
        ["networking-cables"] = "network",
        ["control-panels-enclosures"] = "panel",
        ["water-etp-process"] = "process",
        ["process-measurement"] = "process",
        ["safety-test-tools"] = "safety"
    };

    private static readonly Dictionary<string, string[]> Positive = new()
    {
        ["plc"] = ["Programmed it in an afternoon and it has been running without a single fault since.", "Scan time is very short and the onboard I/O was enough for our whole machine.", "Communication with the HMI and drive worked on the first try.", "Memory and instruction speed are more than we need for this application.", "Firmware was current and the unit arrived with all terminal blocks and a printed quick-start guide."],
        ["hmi"] = ["The screen is bright and the touch response is accurate even with gloves on.", "Connected to our PLC over Modbus in minutes; the driver list is long.", "Recipes and alarm history work exactly as described and the operators like the layout.", "Brightness and viewing angle are good under our workshop lighting.", "Panel cutout matched the datasheet, the clips hold it firmly."],
        ["drive"] = ["Auto-tune found the motor parameters correctly and the motor runs smooth at low speed.", "Replaced an old drive on our conveyor and energy bill dropped noticeably.", "Keypad menus are logical, and the Modbus link to the PLC works as documented.", "No tripping on start-up even with a high-inertia load.", "Runs cool with proper clearance, cooling fan only starts when needed."],
        ["sensor"] = ["Detection is stable, no false triggers even with vibration on the conveyor.", "LED indicator is clear and the sensing distance matches the datasheet.", "Wiring was straightforward and the body is solid, no loosening after weeks of vibration.", "Response time is quick enough for our high-speed counting.", "Switching point has not drifted since installation."],
        ["motor"] = ["Starts smoothly, runs quiet and stays within temperature limits at full load.", "Nameplate data matches what was ordered and the shaft dimensions are accurate.", "Bearings are good, no noise or vibration after months of continuous running.", "Efficiency is noticeably better than the old motor, our meter shows lower current.", "Paint and finish are good, terminal box is easy to wire."],
        ["power"] = ["Switching action is crisp, terminals are well made and tighten properly.", "Fits our DIN-rail layout perfectly and the markings are clear.", "No heating or buzzing after weeks of continuous load.", "Trip characteristics match what the datasheet states.", "Output is stable and ripple is very low, our PLC and sensors run without noise issues."],
        ["pneumatic"] = ["Moves smoothly, no stick-slip even at low speed and the cushioning works well.", "Seals well; we measured no leakage after a week.", "Push-in connection is firm and releases cleanly when needed.", "Response time of the valve is fast, cycle time improved.", "Flow is consistent and the regulator holds pressure steadily."],
        ["network"] = ["Plug-and-play, no dropped packets even with heavy traffic on the line.", "Metal housing and DIN mounting are very sturdy, handles the heat in our panel room.", "Configuration through the web interface was simple and the redundancy ring recovered quickly in testing.", "Cable is flexible but tough, termination was easy.", "Gateway converted the protocol without any packet loss during a week of logging."],
        ["panel"] = ["Quality of the sheet metal and paint is excellent, door closes tight with a good seal.", "Mounting plate and accessories fit without any drilling surprises.", "Terminals grip well and the markers click in firmly.", "Rail is straight and the holes line up with standard accessories.", "Dimensions match the datasheet exactly, easy to plan the layout."],
        ["process"] = ["Readings agree with our reference to within spec and it responds quickly.", "Installation took less than an hour and the 4–20 mA output matches the PLC scaling.", "Very stable measurement even with fluctuating process conditions.", "Calibration holds, we have not had to adjust it since commissioning.", "Display is easy to read and the menu is simple to navigate."],
        ["safety"] = ["Solid construction, contacts are positive-opening and the reset behaves exactly as expected during validation.", "Passed our safety audit without any issue.", "Readings are accurate and match our calibrated reference.", "Fan moves plenty of air and runs quiet, the panel temperature dropped by several degrees.", "Build quality is excellent and the case protects it well on site."]
    };

    private static readonly Dictionary<string, string[]> Neutral = new()
    {
        ["plc"] = ["Works fine, though the manual could be clearer for first-time users.", "Does the job; I wish the onboard memory were a little larger."],
        ["hmi"] = ["Works well but the editing software has a learning curve.", "Display is good, but the viewing angle drops off a bit from the sides."],
        ["drive"] = ["Performs as specified; parameter list is long and takes time to learn.", "Good drive, but fan noise is noticeable in a quiet room."],
        ["sensor"] = ["Reliable so far; the mounting nut is a bit thin.", "Does what it should, cable could be longer."],
        ["motor"] = ["Runs well; a bit heavier than expected.", "Good motor, the terminal box cover needs a gentle fit."],
        ["power"] = ["Works as described; terminals are slightly tight for thick wires.", "Fine for the price, case is plastic rather than metal."],
        ["pneumatic"] = ["Works well, needs a little break-in before it moves smoothly.", "Good quality, a few fittings arrived without sealing tape."],
        ["network"] = ["Does the job; the web interface feels dated.", "Reliable, though the mounting clip is stiff."],
        ["panel"] = ["Good product, paint chipped slightly at one corner during handling.", "Sturdy, but the manual is minimal."],
        ["process"] = ["Accurate, but the menu is not very intuitive.", "Works well; it takes a while to stabilise after power-up."],
        ["safety"] = ["Does what it says; instructions are brief.", "Good tool, the carry case is a bit flimsy."]
    };

    private static readonly Dictionary<string, string[]> Negative = new()
    {
        ["plc"] = ["One digital input channel was dead on arrival and had to be replaced.", "The firmware on the unit was older than advertised and needed updating before it would talk to the software."],
        ["hmi"] = ["Touch calibration drifted after a week and needed re-calibrating.", "Screen had a small bright pixel cluster from day one."],
        ["drive"] = ["Showed an over-current fault at start-up until I changed the acceleration time; the manual should say this more clearly.", "The cooling fan is louder than the previous model I used."],
        ["sensor"] = ["The cable insulation was cut slightly on arrival, so I had to re-terminate it.", "Sensing distance is shorter than the datasheet figure on aluminium targets."],
        ["motor"] = ["Slight bearing noise on first run; improved after a few hours but not perfect.", "Delivered with a cracked terminal box lid."],
        ["power"] = ["Rattles slightly, one terminal screw would not tighten properly.", "Runs warm even at half load."],
        ["pneumatic"] = ["Minor air leak at one port out of the box.", "Cylinder stroke was longer than listed so I had to re-order."],
        ["network"] = ["One port was unstable and kept dropping the link; replaced by the seller.", "Mounting clip broke on the first install."],
        ["panel"] = ["Door gasket was not seated properly at one corner.", "Mounting plate arrived bent."],
        ["process"] = ["Reading offset out of the box, needed calibration before use.", "Display backlight flickers occasionally."],
        ["safety"] = ["One contact did not release reliably during testing; replaced under warranty.", "Fan produced a rattling sound until I tightened the mounting screws."]
    };

    private static readonly string[] DeliveryGood =
    [
        "Delivery to {city} took two days and the packing was solid.", "Arrived a day earlier than promised.", "Courier handled it well, box was intact.",
        "Delivered in {city} within 3 days with tracking updates.", "Packed in a double box with foam, no damage at all.", "Same-day dispatch, arrived next morning."
    ];

    private static readonly string[] DeliveryBad =
    [
        "Delivery took nearly a week longer than quoted.", "The outer carton was dented, though the product itself was fine.", "Courier delay of three days, but the seller kept me informed.",
        "Had to follow up twice before it was dispatched."
    ];

    private static readonly string[] Support =
    [
        "The seller answered my technical questions the same day.", "Got the datasheet and wiring diagram on WhatsApp within minutes.", "Warranty claim was settled without any argument.",
        "Their engineer helped me with the parameter settings over the phone.", "VAT invoice was issued correctly."
    ];

    private static readonly string[] Genuine =
    [
        "Genuine product with a proper manufacturer label and serial number.", "Verified the serial number with the manufacturer, it is authentic.", "Original packing and warranty card included.",
        "Looks and feels exactly like the units we import ourselves."
    ];

    private static readonly string[] PriceGood =
    [
        "Price was better than other local suppliers.", "Good value compared with the import price.", "A fair price for an original unit.", "Reasonable price and quick quote."
    ];

    private static readonly string[] PriceBad = ["A little pricey, but the quality justifies it.", "Price is on the higher side compared with grey-market options."];

    private static readonly string[] ClosersGood =
    [
        "Highly recommended.", "Will order again for our next project.", "Five stars from our maintenance team.", "Would recommend to other factory engineers.",
        "Already ordered two more.", "Exactly what we needed.", "No complaints.", "Happy with the purchase.", "Solid product, easy decision.", "Will definitely buy from this seller again."
    ];

    private static readonly string[] ClosersNeutral = ["Overall decent.", "Good enough for the price.", "Satisfied overall, with minor improvements possible.", "Does what it should."];

    private static readonly string[] ClosersBad = ["Disappointed.", "Hope the quality improves.", "Not what I expected.", "Would not buy again unless the issue is fixed.", "Fine after replacement, but the delay hurt our schedule."];

    private static readonly string[] Openers =
    [
        "Bought for our {industry} plant in {city}.", "Installed on a {industry} line.", "Ordered as a spare for our {industry} unit.", "Used on a {industry} machine in {city}.",
        "Purchased for a panel build for a {industry} client.", "Replacement for an old unit at our {industry} factory.", "We needed this for a retrofit project.", "Commissioned last month at our {city} site."
    ];

    private static readonly string[] ShortGood = ["Excellent quality.", "Works perfectly.", "Very good product.", "As described, fast delivery.", "Great value.", "Genuine and reliable.", "Perfect fit.", "Runs flawlessly."];
    private static readonly string[] ShortNeutral = ["Okay product.", "Decent for the price.", "Works as expected.", "Average."];
    private static readonly string[] ShortBad = ["Not satisfied.", "Had an issue on arrival.", "Below expectations.", "Needs improvement."];

    private static readonly string[] TitlesGood = ["Excellent quality", "Works perfectly", "Very reliable", "Great value for money", "Solid build", "Exactly as described", "Highly recommended", "Fast delivery, genuine product", "Stable and accurate", "Best choice for our line"];
    private static readonly string[] TitlesNeutral = ["Good, with small remarks", "Decent product", "Does the job", "OK for the price"];
    private static readonly string[] TitlesBad = ["Disappointed", "Issue on arrival", "Not as expected", "Needs improvement", "Replaced by seller"];

    private static readonly string[] BanglaGood =
    [
        "পণ্যটি আসল এবং খুব ভালো কাজ করছে। ডেলিভারি দ্রুত পেয়েছি।", "মান খুব ভালো, প্যাকিংও চমৎকার ছিল। আবার কিনব।", "আমাদের ফ্যাক্টরিতে লাগিয়েছি, কোনো সমস্যা ছাড়াই চলছে।",
        "দামের তুলনায় অনেক ভালো। বিক্রেতা খুব সহযোগিতা করেছে।", "অরিজিনাল প্রোডাক্ট, ওয়ারেন্টি কার্ডসহ পেয়েছি।"
    ];

    private static readonly string[] BanglaNeutral = ["পণ্য মোটামুটি ভালো, তবে ডেলিভারিতে একটু দেরি হয়েছে।", "কাজ চলছে, ম্যানুয়ালটা আরেকটু পরিষ্কার হলে ভালো হতো।"];
    private static readonly string[] BanglaBad = ["প্যাকেট আসার সময় একটু ক্ষতিগ্রস্ত ছিল, সমস্যা সমাধান করতে সময় লেগেছে।", "আশা অনুযায়ী ভালো কাজ করেনি, বিক্রেতার সঙ্গে কথা বলছি।"];

    private static readonly string[] BanglishGood =
    [
        "Product ta original, kaj bhalo kortese. Delivery 2 din e peyechi.", "Quality onek bhalo, packing o solid chilo. Abar order korbo.", "Amader factory te lagiyechi, kono problem nai. Seller helpful.",
        "Price er compare e onek valo. Recommend korbo."
    ];

    private static readonly string[] BanglishNeutral = ["Product moti-muti valo, kintu delivery te ektu deri hoyechilo.", "Kaj kortese, manual ta aro clear hole valo hoto."];
    private static readonly string[] BanglishBad = ["Packet ashar shomoy ektu damage chilo, replace korte somoy legeche.", "Expect kora moto valo kaj kore nai."];

    private static readonly string[] ModelMentions =
    [
        "Ordered {qty} of the {model} for our {industry} line.", "The {model} did exactly what we needed.", "Second time buying the {model}.",
        "Used the {model} on a {industry} machine.", "Fitted {qty} x {model} during our last shutdown.", "Compared the {model} with two alternatives before buying.",
        "The {model} matched our spare-parts list."
    ];

    private static string Fill(string template, string city, string industry) =>
        template.Replace("{city}", city).Replace("{industry}", industry);

    private static string Mention(SeedRandom rng, string model, string industry) =>
        rng.Pick(ModelMentions).Replace("{model}", model).Replace("{industry}", industry).Replace("{qty}", rng.Next(1, 13).ToString());

    /// <summary>Composes a review (title, body) whose tone follows <paramref name="rating"/>.</summary>
    public static (string? Title, string Body) Compose(SeedRandom rng, string domain, int rating, string city, string industry, string role, string model)
    {
        // Language mix: ~88 % English, 6 % Bangla, 6 % Banglish.
        var lang = rng.NextDouble();
        if (lang > 0.94)
        {
            var bank = rating >= 4 ? BanglishGood : rating == 3 ? BanglishNeutral : BanglishBad;
            return (null, rng.Pick(bank) + (rng.Chance(0.6) ? $" ({model}, {city})" : string.Empty));
        }

        if (lang > 0.88)
        {
            var bank = rating >= 4 ? BanglaGood : rating == 3 ? BanglaNeutral : BanglaBad;
            return (null, rng.Pick(bank) + (rng.Chance(0.6) ? $" ({model}, {city})" : string.Empty));
        }

        var tone = rating >= 4 ? 0 : rating == 3 ? 1 : 2;
        var titles = tone == 0 ? TitlesGood : tone == 1 ? TitlesNeutral : TitlesBad;
        var title = rng.Chance(0.7) ? rng.Pick(titles) : null;

        // Length bucket: 22 % one-liners, 48 % medium, 30 % detailed.
        var roll = rng.NextDouble();
        if (roll < 0.22)
        {
            var bank = tone == 0 ? ShortGood : tone == 1 ? ShortNeutral : ShortBad;
            return (title, rng.Pick(bank) + (rng.Chance(0.6) ? " " + Mention(rng, model, industry) : string.Empty));
        }

        var parts = new List<string>();
        var detailed = roll >= 0.70;
        if (detailed || rng.Chance(0.35)) parts.Add(Fill(rng.Pick(Openers), city, industry));
        if (detailed && rng.Chance(0.25)) parts.Add($"I'm a {role}.");

        string[] domainBank = tone == 0 ? Positive[domain] : tone == 1 ? Neutral[domain] : Negative[domain];
        if (rng.Chance(0.4)) parts.Add(Mention(rng, model, industry));
        parts.Add(rng.Pick(domainBank));
        if (detailed && tone == 0) parts.Add(rng.Pick(Positive[domain]));
        if (tone == 2 && rng.Chance(0.5)) parts.Add(rng.Pick(Support));

        if (rng.Chance(detailed ? 0.8 : 0.45)) parts.Add(Fill(rng.Pick(tone == 2 ? DeliveryBad : DeliveryGood), city, industry));
        if (tone == 0 && rng.Chance(0.35)) parts.Add(rng.Pick(Genuine));
        if (tone == 0 && rng.Chance(detailed ? 0.4 : 0.2)) parts.Add(rng.Pick(Support));
        if (rng.Chance(0.3)) parts.Add(rng.Pick(tone == 2 ? PriceBad : PriceGood));
        parts.Add(rng.Pick(tone == 0 ? ClosersGood : tone == 1 ? ClosersNeutral : ClosersBad));

        return (title, string.Join(' ', parts.Distinct()));
    }

    // ───────────────────────── Q&A ─────────────────────────

    public sealed record QaPair(string Question, string SellerAnswer);

    private static readonly QaPair[] GenericQa =
    [
        new("Is this brand new and genuine?", "Yes, it is brand new in the manufacturer's original packing with warranty."),
        new("Do you provide a VAT invoice?", "Yes, a VAT invoice (Mushak) is provided with every order."),
        new("How long will delivery to {city} take?", "Inside Dhaka 1–2 working days; outside Dhaka 3–5 working days by courier, with tracking."),
        new("Is a datasheet available?", "Yes, we can share the manufacturer datasheet and wiring diagram on request."),
        new("Do you offer a discount for bulk orders?", "Yes, please message us your quantity and we will send a quotation within the same working day."),
        new("What warranty is provided?", "Manufacturer warranty applies as shown on the product page, handled by us locally."),
        new("Can I pay by bank transfer or bKash?", "Yes, bank transfer, bKash and card payments are all accepted at checkout."),
        new("Is this item in stock now?", "The stock status shown on the page is live; items marked in stock ship the same or next working day.")
    ];

    private static readonly Dictionary<string, QaPair[]> DomainQa = new()
    {
        ["plc"] = [new("Is the programming cable included?", "No, the programming cable and software are sold separately; the unit is supplied with its terminal blocks."), new("Can I add more I/O later?", "Yes, within the expansion limits of the series — check the datasheet for the maximum number of modules.")],
        ["hmi"] = [new("Does it support Modbus?", "Yes — see the 'Interfaces' line in the specifications for the supported ports and protocols."), new("Are the mounting clips included?", "Yes, the panel-mounting clips come in the box.")],
        ["drive"] = [new("Can it run a single-phase motor?", "No. AC drives output three-phase power, so use a three-phase motor."), new("Is the keypad included?", "Yes, a keypad is included; communication and extension cables are optional.")],
        ["sensor"] = [new("Is it suitable for a 24 V DC supply?", "Yes, 24 V DC is within the supply range listed in the specifications."), new("Is a mounting bracket included?", "Cylindrical sensors ship with two lock-nuts; brackets are sold separately.")],
        ["motor"] = [new("Is it suitable for 400 V 50 Hz?", "Yes, it is rated for the voltage and frequency shown in the specifications."), new("Can it be used with a VFD?", "Yes, it can be driven by a frequency inverter; keep within the speed range allowed by the motor's cooling.")],
        ["power"] = [new("Does it mount on a 35 mm DIN rail?", "Yes, it clips onto a standard 35 mm DIN rail."), new("Is a spare terminal or accessory included?", "Only what is listed on the page — accessories are sold separately.")],
        ["pneumatic"] = [new("What air pressure does it need?", "Use clean, dry compressed air within the pressure range given in the specifications, with a filter/regulator upstream."), new("Is the stroke selectable?", "If the product has a stroke or size option, choose it from the variant selector before adding to cart.")],
        ["network"] = [new("Does it need configuration?", "Unmanaged switches are plug-and-play; managed models are configured through the web interface."), new("Is a power supply included?", "No, a 24 V DC DIN-rail power supply is sold separately.")],
        ["panel"] = [new("Is the mounting plate included?", "Enclosures with a stated mounting plate include it; otherwise it is an optional accessory."), new("Can I get a custom cut-out?", "We can arrange custom cut-outs for bulk orders — message us your drawing.")],
        ["process"] = [new("Does it have a 4–20 mA output?", "Please check the output line in the specifications for the exact signal types supported."), new("Is calibration included?", "Factory calibration is included; field calibration can be arranged.")],
        ["safety"] = [new("Is it certified for use in safety circuits?", "Safety devices carry the manufacturer's certifications shown in the datasheet; always validate the full safety function."), new("Do you supply spare parts?", "Yes, spare parts and accessories are available on request.")]
    };

    public static IReadOnlyList<QaPair> QuestionsFor(string domain) => [.. GenericQa, .. DomainQa[domain]];

    public static string Customer(SeedRandom rng, string city) => rng.Pick(
    [
        "I bought this last month and it works well for our application.",
        $"I ordered it for my panel in {city}; arrived in good condition.",
        "Mine came with the original warranty card, no issues so far.",
        "Used it on a similar machine, works reliably."
    ]);

    public static string FillCity(string text, string city) => text.Replace("{city}", city);
}
