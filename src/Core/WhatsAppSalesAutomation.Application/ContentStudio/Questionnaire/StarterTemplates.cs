using System.Text.Json;

namespace WhatsAppSalesAutomation.Application.ContentStudio.Questionnaire;

/// <summary>One questionnaire template the platform starts with: who it is for and what it asks. Industry null = the generic template.</summary>
public sealed record StarterTemplate(string Key, string Title, string Description, string? Industry, QuestionnaireDefinition Definition);

/// <summary>
/// The questionnaires every environment starts with, as data the seeder inserts once and a platform admin then owns and versions - the core
/// questionnaire engine knows nothing of any industry. Each template opens with the same business-profile questions (name, industry,
/// description, audience, offerings, locations, brand, language), then asks what only that kind of business has to say, and ends with the
/// questions every business shares (offers, prices, customer results). What each answer becomes is a fact rule, so the vocabulary of fact
/// categories ("About", "Services", "Pricing"...) is what the readiness rules match on.
/// </summary>
public static class StarterTemplates
{
    private const string GroupAbout = "About your business";
    private const string GroupCustomers = "Your customers";
    private const string GroupOffer = "What you offer";
    private const string GroupPlaces = "Where you work";
    private const string GroupBrand = "Your brand";
    private const string GroupContact = "How to reach you";

    public static IReadOnlyList<StarterTemplate> All() => new[] { Generic(), Healthcare(), Software(), Manufacturing() };

    // ----- the templates ---------------------------------------------------------------------------------------------------------------

    private static StarterTemplate Generic() => new(
        "generic-business", "Business questionnaire", "Tells the studio who you are, what you offer and how you sound. Used for any business without a more specific questionnaire.", null,
        Build(
            Profile(Text("products_services", GroupOffer, "What are your main products or services?", longText: true, required: true,
                help: "List the things you sell or do. A short list is fine.", example: "Interior design for homes, modular kitchens, wardrobes.",
                fact: Fact("Services", "We offer: {value}"))),
            Array.Empty<QuestionDef>(),
            Tail()));

    private static StarterTemplate Healthcare() => new(
        "healthcare", "Healthcare questionnaire", "For clinics, hospitals and other healthcare providers, including eye-care clinics: services, appointments and what a visit involves.", "Healthcare",
        Build(
            Profile(Choice("products_services", GroupOffer, "Which of these does your practice offer?", multi: true, required: true,
                help: "Tick everything that applies. You can add anything else in the next question.", example: "Eye care, general consultation",
                fact: Fact("Services", "We offer {value}."),
                options: O(("general_consultation", "General consultation"), ("specialist_consultation", "Specialist consultation"), ("diagnostics", "Diagnostic tests and health check-ups"),
                    ("surgery", "Surgery and procedures"), ("eye_care", "Eye care"), ("dental", "Dental care"), ("physiotherapy", "Physiotherapy"),
                    ("teleconsultation", "Teleconsultation (online consults)"), ("vaccination", "Vaccination")))),
            new[]
            {
                Text("services_other", GroupOffer, "Anything else you offer that is not in the list?", example: "Hearing tests, nutrition counselling.",
                    fact: Fact("Services", "We also offer: {value}")),

                // Consultations and appointments - asked of any practice that sees patients.
                Choice("appointment_booking", "Appointments", "How can patients book an appointment?", multi: true, required: true,
                    help: "Tick every way a patient can reach you to book.", fact: Fact("Process", "Appointments can be booked by {value}."),
                    options: O(("phone", "Phone call"), ("whatsapp", "WhatsApp"), ("website", "Our website"), ("walk_in", "Walk in without booking"))),
                Text("appointment_notice", "Appointments", "How early should patients book?", example: "Same-day slots are usually available; weekends need a day's notice.",
                    showWhen: Answered("appointment_booking"), fact: Fact("Process", "Booking notice: {value}")),
                Text("consultation_fee", "Consultation", "What is your consultation fee?", help: "Say what a first visit costs. A range is fine.",
                    example: "Rs 500 for the first visit, Rs 300 for follow-ups.",
                    showWhen: AnyOf(Has("products_services", "general_consultation"), Has("products_services", "specialist_consultation"), Has("products_services", "eye_care"), Has("products_services", "dental")),
                    fact: Fact("Pricing", "Consultation fee: {value}", validDays: 180)),
                Text("first_visit", "Consultation", "What happens at a first visit?", longText: true,
                    help: "Walk through it the way you would tell a new patient.", example: "Registration, a short history, the examination, and a discussion of the next steps. About 30 minutes.",
                    showWhen: AnyOf(Has("products_services", "general_consultation"), Has("products_services", "specialist_consultation"), Has("products_services", "eye_care"), Has("products_services", "dental")),
                    fact: Fact("Process", "A first visit: {value}")),

                // Eye care - asked only when the practice does eye care.
                Choice("eye_services", "Eye care", "Which eye-care services do you provide?", multi: true, required: true, showWhen: Has("products_services", "eye_care"),
                    help: "Tick everything you do in your own clinic.", fact: Fact("Services", "Our eye-care services are {value}."),
                    options: O(("eye_exam", "Comprehensive eye exam"), ("cataract", "Cataract surgery"), ("refractive", "LASIK and other vision correction"), ("glaucoma", "Glaucoma care"),
                        ("retina", "Retina care"), ("paediatric", "Children's eye care"), ("contact_lenses", "Contact lenses"), ("spectacles", "Spectacles and frames"))),
                Text("eye_technology", "Eye care", "Is there any special equipment or technology you use?", showWhen: Has("products_services", "eye_care"),
                    example: "Digital retina imaging and an automated field analyser.", fact: Fact("Features", "Eye-care equipment: {value}")),
                Text("cataract_details", "Eye care", "For cataract surgery: how long does it take and what is recovery like?", longText: true,
                    showWhen: Has("eye_services", "cataract"), example: "About 20 minutes with local anaesthesia; most patients go home the same day and resume normal activity in a week.",
                    fact: Fact("Process", "Cataract surgery: {value}")),

                // Dental and surgery follow-ups.
                Text("dental_details", "Dental care", "Which dental treatments do you do?", showWhen: Has("products_services", "dental"),
                    example: "Cleaning, fillings, root canals, braces and implants.", fact: Fact("Services", "Dental treatments: {value}")),
                Text("surgery_details", "Surgery", "For your main surgery: how long is a typical stay and recovery?", longText: true,
                    showWhen: Has("products_services", "surgery"), fact: Fact("Process", "Surgery and recovery: {value}")),
                Text("doctor_credentials", "Your doctors", "Tell us about your doctors' qualifications and experience.", longText: true,
                    help: "Only what you can show proof of - degrees, registration, years in practice.", example: "Dr A Sharma, MS Ophthalmology, 15 years of experience.",
                    fact: Fact("About", "Our doctors: {value}"))
            },
            Tail()));

    private static StarterTemplate Software() => new(
        "technology-software", "Software questionnaire", "For software and technology companies: features, workflows, integrations and pricing.", "Technology & software",
        Build(
            Profile(Choice("products_services", GroupOffer, "What do you build or sell?", multi: true, required: true,
                help: "Tick everything that applies.", example: "A SaaS product, AI automation",
                fact: Fact("Services", "We offer {value}."),
                options: O(("saas", "A software product (SaaS)"), ("custom_software", "Custom software development"), ("mobile_apps", "Mobile apps"), ("web", "Websites and web apps"),
                    ("ai_automation", "AI and automation"), ("cloud", "Cloud and DevOps"), ("cybersecurity", "Cybersecurity"), ("it_support", "IT support and managed services")))),
            new[]
            {
                Text("key_features", "Product and features", "What are the main features customers use most?", longText: true, required: true,
                    help: "List them in plain words, as you would to a new customer.", example: "Shared inbox, automatic follow-ups, reports, and a mobile app.",
                    fact: Fact("Features", "Key features: {value}")),
                Text("main_workflow", "Product and features", "Describe the steps a customer follows to get value from it.", longText: true,
                    help: "From sign-up to the first result.", example: "Sign up, connect your WhatsApp number, import contacts, send your first campaign.",
                    fact: Fact("Process", "Typical workflow: {value}")),
                Choice("integrations", "Integrations", "Which tools does it connect with?", multi: true, example: "WhatsApp and a CRM",
                    fact: Fact("Features", "It integrates with {value}."),
                    options: O(("whatsapp", "WhatsApp"), ("crm", "A CRM"), ("payments", "Payment gateways"), ("accounting", "Accounting software"), ("erp", "ERP systems"), ("email", "Email tools"), ("other", "Others"))),
                Text("integrations_other", "Integrations", "Which other tools does it connect with?", showWhen: Has("integrations", "other"),
                    fact: Fact("Features", "Other integrations: {value}")),
                Choice("deployment", "Product and features", "How is it delivered?", multi: false,
                    fact: Fact("Features", "Delivery: {value}."),
                    options: O(("cloud", "In the cloud (hosted by us)"), ("on_premise", "Installed on the customer's own servers"), ("both", "Either"))),
                Choice("pricing_model", "Pricing", "How do you charge?", multi: false, help: "Pick the closest one.",
                    fact: Fact("Pricing", "Pricing model: {value}.", validDays: 180),
                    options: O(("subscription", "A monthly or yearly subscription"), ("one_time", "A one-time price"), ("usage", "Based on usage"), ("quote", "A custom quote for each customer"))),
                Text("starting_price", "Pricing", "What is the starting price?", example: "From Rs 2,999 per month.",
                    showWhen: AnyOf(Eq("pricing_model", "subscription"), Eq("pricing_model", "one_time"), Eq("pricing_model", "usage")),
                    fact: Fact("Pricing", "Starting price: {value}", validDays: 180)),
                Bool("free_trial", "Pricing", "Do you offer a free trial?"),
                Number("trial_days", "Pricing", "How many days is the free trial?", min: 1, max: 365, showWhen: EqBool("free_trial", true),
                    fact: Fact("Offers", "Free trial length in days: {value}", validDays: 180))
            },
            Tail()));

    private static StarterTemplate Manufacturing() => new(
        "manufacturing", "Manufacturing questionnaire", "For manufacturers: product specifications, applications, customisation and delivery.", "Manufacturing",
        Build(
            Profile(Text("products_services", GroupOffer, "What products do you manufacture?", longText: true, required: true,
                help: "List your main product lines.", example: "Stainless steel water tanks, cold-room panels.",
                fact: Fact("Services", "We manufacture: {value}"))),
            new[]
            {
                Text("product_specs", "Your products", "What are the key specifications of your main product?", longText: true, required: true,
                    help: "Sizes, materials, capacity, standards - whatever a buyer asks first.", example: "304-grade stainless steel, 500 to 10,000 litre capacity, 5-year warranty.",
                    fact: Fact("Features", "Specifications: {value}")),
                Text("applications", "Your products", "Where and how do customers use them?", longText: true,
                    example: "Factories, hotels and housing societies for storing drinking water.", fact: Fact("Features", "Typical applications: {value}")),
                Choice("certifications", "Your products", "Which certifications do you hold?", multi: true,
                    help: "Only what you hold today and can show.", fact: Fact("Features", "Certifications: {value}."),
                    options: O(("iso_9001", "ISO 9001"), ("iso_14001", "ISO 14001"), ("ce", "CE marking"), ("bis", "BIS"), ("rohs", "RoHS"), ("none", "None yet"))),
                Choice("customisation", "Orders", "Can customers ask for custom versions?", multi: false,
                    fact: Fact("Process", "Customisation: {value}."),
                    options: O(("standard", "Standard products only"), ("custom_on_request", "Custom versions on request"), ("fully_custom", "Everything is made to order"))),
                Text("customisation_details", "Orders", "What can be customised, and how do customers ask?",
                    showWhen: AnyOf(Eq("customisation", "custom_on_request"), Eq("customisation", "fully_custom")), fact: Fact("Process", "How customisation works: {value}")),
                Text("min_order", "Orders", "What is your minimum order?", example: "50 units, or Rs 1 lakh.", fact: Fact("Pricing", "Minimum order: {value}", validDays: 180)),
                Choice("delivery_regions", "Delivery", "Where do you deliver?", multi: true, fact: Fact("Locations", "We deliver to {value}."),
                    options: O(("local", "My city"), ("state", "My state"), ("national", "All of India"), ("export", "Other countries"))),
                Text("export_countries", "Delivery", "Which countries do you export to?", showWhen: Has("delivery_regions", "export"), fact: Fact("Locations", "We export to: {value}")),
                Text("lead_time", "Delivery", "How long does delivery usually take?", example: "7 working days for standard sizes, 3 weeks for custom.",
                    fact: Fact("Process", "Typical delivery time: {value}"))
            },
            Tail()));

    // ----- the shared parts ------------------------------------------------------------------------------------------------------------

    /// <summary>The business-profile questions every template opens with. The offerings question differs by industry, so it is passed in.</summary>
    private static IEnumerable<QuestionDef> Profile(QuestionDef offerings)
    {
        yield return Text("business_name", GroupAbout, "What is your business called?", required: true, requireKnown: true, prefill: "Name",
            help: "The name customers know you by.", example: "Sunrise Eye Clinic", fact: Fact("About", "The business is called {value}."));
        yield return Text("industry", GroupAbout, "What kind of business is it?", required: true, prefill: "Industry",
            help: "Your industry or field.", example: "Healthcare", fact: Fact("About", "The business is in the {value} industry."));
        yield return Text("business_description", GroupAbout, "In a few sentences, what does your business do?", longText: true, required: true, prefill: "BusinessDescription",
            help: "Write it the way you would tell a new customer. Plain words are best.", example: "We are an eye clinic in Pune. We help adults and children with eye check-ups, cataract surgery and spectacles.",
            fact: Fact("About", "What we do: {value}"));
        yield return Text("target_audience", GroupCustomers, "Who are your customers?", longText: true, required: true, prefill: "TargetAudience",
            help: "Age, place, and what they come to you for.", example: "Working adults aged 30 to 60 in Pune, and parents of school-going children.",
            fact: Fact("Audience", "Our customers are: {value}"));
        yield return offerings;
        yield return Text("service_locations", GroupPlaces, "Which places do you serve?", required: true, prefill: "TargetLocation",
            help: "Cities, areas, or 'online across India'.", example: "Pune and nearby areas", fact: Fact("Locations", "We serve: {value}"));
        yield return Choice("brand_tone", GroupBrand, "How should your brand sound?", multi: true,
            help: "Pick the words that fit you best. It keeps every video sounding like you.", example: "Friendly and trustworthy",
            fact: Fact("Brand", "Our brand tone is {value}."),
            options: O(("friendly", "Friendly"), ("professional", "Professional"), ("premium", "Premium"), ("caring", "Caring"), ("trustworthy", "Trustworthy"), ("playful", "Playful"), ("expert", "Expert")));
        yield return Text("brand_identity", GroupBrand, "Anything that makes your brand recognisable?",
            help: "Colours, a tagline, a logo description.", example: "Blue and white, tagline 'Seeing is living'.", fact: Fact("Brand", "Brand identity: {value}"));
        yield return Choice("content_language", GroupBrand, "Which language should your content be in?", multi: true, required: true,
            help: "Pick one or more. Videos are written and voiced in these.", example: "English, Hindi",
            fact: Fact("Language", "Content is created in {value}."),
            options: O(("english", "English"), ("hindi", "Hindi"), ("hinglish", "Hinglish"), ("marathi", "Marathi"), ("gujarati", "Gujarati"), ("tamil", "Tamil"), ("telugu", "Telugu"),
                ("bengali", "Bengali"), ("kannada", "Kannada"), ("malayalam", "Malayalam"), ("punjabi", "Punjabi")));
        yield return new QuestionDef
        {
            Key = "contact_details", Type = QuestionType.Structured, Group = GroupContact, Prompt = "How can customers reach you?",
            HelpText = "Fill in whatever you want shown in videos.",
            Fields =
            {
                new FieldDef { Key = "phone", Label = "Phone", Type = QuestionType.ShortText },
                new FieldDef { Key = "website", Label = "Website", Type = QuestionType.ShortText },
                new FieldDef { Key = "email", Label = "Email", Type = QuestionType.ShortText },
                new FieldDef { Key = "address", Label = "Address", Type = QuestionType.ShortText }
            },
            Fact = Fact("Contact", "Contact details - {value}.")
        };
        yield return new QuestionDef
        {
            Key = "opening_hours", Type = QuestionType.Structured, Group = GroupContact, Prompt = "When are you open?",
            HelpText = "Leave a day blank if you are closed.",
            Fields =
            {
                new FieldDef { Key = "weekdays", Label = "Monday to Friday", Type = QuestionType.ShortText },
                new FieldDef { Key = "saturday", Label = "Saturday", Type = QuestionType.ShortText },
                new FieldDef { Key = "sunday", Label = "Sunday", Type = QuestionType.ShortText }
            },
            Fact = Fact("Contact", "Opening hours - {value}.")
        };
    }

    /// <summary>The questions every business shares: a current offer, prices, and a real customer result (with the permission to share it).</summary>
    private static IEnumerable<QuestionDef> Tail()
    {
        yield return Bool("has_offer", "Offers and prices", "Do you have an offer or discount running right now?");
        yield return Text("offer_details", "Offers and prices", "What is the offer, and until when?", required: true, showWhen: EqBool("has_offer", true),
            help: "Include the end date so we never promote an offer that has finished.", example: "Free eye check-up for senior citizens until 31 December.",
            fact: Fact("Offers", "Current offer: {value}", validDays: 90));
        yield return Text("price_info", "Offers and prices", "What do your main products or services cost?",
            help: "A range or a starting price is fine. Leave it out if you would rather not publish prices.", example: "Consultation Rs 500; cataract surgery from Rs 25,000 per eye.",
            fact: Fact("Pricing", "Prices: {value}", validDays: 180));
        yield return Bool("has_customer_story", "Customer results", "Do you have a real customer result you could talk about, with their permission?",
            help: "For example a patient or client whose outcome you can describe and prove.");
        yield return Text("case_study_result", "Customer results", "What was the result?", longText: true, required: true, showWhen: EqBool("has_customer_story", true),
            help: "Say what happened, roughly when, and what changed. Only true, documented outcomes.", example: "A patient's reading vision improved from 6/36 to 6/9 after cataract surgery in March.",
            fact: Fact("Results", "Customer result: {value}"));
        yield return Choice("case_study_consent", "Customer results", "Has the customer agreed to be featured?", multi: false, required: true, showWhen: EqBool("has_customer_story", true),
            help: "We will ask you how you can show this afterwards.", fact: Fact("Consent", "Customer permission to feature them: {value}."),
            options: O(("written", "Yes, in writing"), ("verbal", "Yes, verbally"), ("no", "Not yet")));
    }

    // ----- small builders --------------------------------------------------------------------------------------------------------------

    private static QuestionnaireDefinition Build(IEnumerable<QuestionDef> profile, IEnumerable<QuestionDef> specific, IEnumerable<QuestionDef> tail) =>
        new() { Questions = profile.Concat(specific).Concat(tail).ToList() };

    private static (string Value, string Label)[] O(params (string Value, string Label)[] options) => options;

    private static FactRule Fact(string category, string template, string? subject = null, int? validDays = null) =>
        new() { Category = category, Template = template, Subject = subject, ValidDays = validDays };

    private static QuestionDef Text(
        string key, string group, string prompt, bool longText = false, bool required = false, bool requireKnown = false, string? prefill = null,
        string? help = null, string? example = null, ConditionDef? showWhen = null, FactRule? fact = null) => new()
    {
        Key = key, Group = group, Prompt = prompt, Type = longText ? QuestionType.LongText : QuestionType.ShortText, Required = required, RequireKnown = requireKnown,
        PrefillFrom = prefill, HelpText = help, Example = example, ShowWhen = showWhen, Fact = fact
    };

    private static QuestionDef Choice(
        string key, string group, string prompt, bool multi, (string Value, string Label)[] options, bool required = false, string? help = null, string? example = null,
        ConditionDef? showWhen = null, FactRule? fact = null) => new()
    {
        Key = key, Group = group, Prompt = prompt, Type = multi ? QuestionType.MultiChoice : QuestionType.SingleChoice, Required = required,
        HelpText = help, Example = example, ShowWhen = showWhen, Fact = fact, Options = options.Select(o => new OptionDef { Value = o.Value, Label = o.Label }).ToList()
    };

    private static QuestionDef Bool(string key, string group, string prompt, bool required = false, string? help = null, ConditionDef? showWhen = null) => new()
    {
        Key = key, Group = group, Prompt = prompt, Type = QuestionType.Boolean, Required = required, HelpText = help, ShowWhen = showWhen
    };

    private static QuestionDef Number(
        string key, string group, string prompt, decimal min, decimal max, bool required = false, ConditionDef? showWhen = null, FactRule? fact = null) => new()
    {
        Key = key, Group = group, Prompt = prompt, Type = QuestionType.Number, Min = min, Max = max, Required = required, ShowWhen = showWhen, Fact = fact
    };

    private static ConditionDef Eq(string key, string value) =>
        new() { QuestionKey = key, Operator = ConditionOperator.Equal, Value = JsonSerializer.SerializeToElement(value) };

    private static ConditionDef EqBool(string key, bool value) =>
        new() { QuestionKey = key, Operator = ConditionOperator.Equal, Value = JsonSerializer.SerializeToElement(value) };

    private static ConditionDef Has(string key, string value) =>
        new() { QuestionKey = key, Operator = ConditionOperator.Contains, Value = JsonSerializer.SerializeToElement(value) };

    private static ConditionDef Answered(string key) => new() { QuestionKey = key, Operator = ConditionOperator.Answered };

    private static ConditionDef AnyOf(params ConditionDef[] conditions) => new() { Any = conditions.ToList() };
}
