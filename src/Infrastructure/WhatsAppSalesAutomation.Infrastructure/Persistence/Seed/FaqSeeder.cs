using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotently inserts the platform's starter FAQ catalog by exact <see cref="FaqEntry.Question"/>
/// text - run once at startup, same "always runs, insert-only" shape as <see cref="PlanSeeder"/>: this
/// is real customer-facing content every environment needs, not a Seed:* gated dev convenience, and a
/// PlatformSuperAdmin may since have edited or reworded a seeded entry through the Content Management
/// System screen, so this never touches an existing row again once its Question exists.
///
/// <see cref="FaqEntry.CreatedByUserId"/> has no real actor at startup (this runs before any HTTP
/// request, so there is no authenticated PlatformSuperAdmin to attribute it to) - <see cref="Guid.Empty"/>
/// is used as the "seeded by the platform itself" placeholder, same as an unset audit actor elsewhere.
/// </summary>
public static class FaqSeeder
{
    private static readonly (string Category, string Question, string Answer)[] Catalog =
    {
        ("Refunds",
            "Agar maine Credit Pack khareeda aur use nahi kiya, to refund milega?",
            "Haan. Purchase date se 30 din ke andar refund request kar sakte hain, lekin sirf unused units ka refund milta hai, us hi price par jis par khareeda tha.\n\nExample: 1,000 WhatsApp messages ka pack $20 mein liya, 800 units bache hain -> refund = (800/1000) x $20 = $16."),

        ("Refunds",
            "Subscription plan cancel karne par refund kaise milta hai?",
            "Payment date se 7 din ke andar request kar sakte hain, par 2 sharten hain: (1) aapne quota ka 10% se zyada use na kiya ho, (2) amount prorate hota hai - 30-din ke period mein jitne din bache hain, us hisaab se.\n\nExample: $99 wala plan, 21 din bache hain 30 mein se -> refund ~= (21/30) x $99 = $69.30. Refund approve hote hi subscription cancel ho jaata hai."),

        ("Refunds",
            "Refund request approve hone mein kitna time lagta hai?",
            "Platform ke Super Admin team review karti hai - wo approve ya reject karte hain. Agar 14 din tak koi decision nahi aata, request khud-ba-khud \"Expired\" ho jaati hai aur held units wapas release ho jaate hain. Aap khud bhi Requested state mein request cancel kar sakte hain."),

        ("Refunds",
            "Kya har company refund request kar sakti hai?",
            "Nahi - ye per-company on/off feature hai. Platform Admin har company ke liye \"Refund Requests\" feature enable ya disable kar sakta hai. Agar disable hai, to us company ke users request hi nahi bhej payenge."),

        ("Billing",
            "WhatsApp message bhejne ka charge kaise calculate hota hai?",
            "Ye ek prepaid quota/credit system hai - har WhatsApp Template message bhejne par account se quota consume hota hai, alag se bill nahi banta. Sirf wahi messages count hote hain jo WhatsApp ne successfully accept kiya ho (Sent/Delivered/Read) - fail ya queued messages ka koi cost nahi. Customer ke 24-hour window ke andar free-form reply ka bhi koi charge nahi lagta."),

        ("Billing",
            "Kya sabhi template messages ka same cost hota hai?",
            "Nahi. Template ki category ke hisaab se weight alag hota hai:\n- Marketing template = 1.0 unit\n- Authentication template ~= 0.54 unit\n- Utility template (order updates, reminders) ~= 0.16 unit\n\nExample: 500 units wale plan mein aap 500 Marketing messages, ya lagbhag 3,000 Utility messages, ya inka mix bhej sakte hain - jab tak total weight 500 se kam rahe."),

        ("Billing",
            "AI se automatic reply aur Lead Discovery ka bhi quota lagta hai?",
            "Haan. Har AI-generated reply \"AI Conversations\" quota se 1 unit consume karta hai. Isi tarah Lead Discovery mein har candidate business jo evaluate hota hai (qualify ho ya reject), wo \"Lead Candidates\" quota se 1 unit consume karta hai."),

        ("Billing",
            "Agar mera quota khatam ho jaaye to kya hoga?",
            "Automatic overage charge nahi lagta - extra paisa apne aap nahi katega. Iske bajaye:\n- Campaign ke pending messages \"Pending\" state mein ruk jaate hain aur credits khareedne ya plan renew hone par apne aap resume ho jaate hain.\n- Manual template send karne par error milta hai: \"Aapka WhatsApp quota khatam ho gaya hai - credits khareedein ya plan renewal ka wait karein.\"\n- AI-conversation quota khatam hone par naya inbound message automatically ek human agent ko handoff ho jaata hai."),

        ("WhatsApp Setup",
            "Apna WhatsApp Business account platform se kaise connect karein?",
            "Ye \"bring your own WhatsApp Business Account\" (BYO-WABA) model hai:\n1. Meta for Developers par ek App banayein aur usmein \"WhatsApp\" product add karein.\n2. Wahan se Phone Number ID aur WhatsApp Business Account ID (WABA ID) milega.\n3. Ek Access Token generate karein - best practice hai ek System User token banana, jo kabhi expire nahi hota (temporary user token sirf ~60 din chalta hai).\n4. App Secret bhi le lein (webhook signature verify karne ke kaam aata hai).\n5. In sabko humare platform ke WhatsApp Settings page mein daal dein.\n\nJaise hi Phone Number ID, Access Token aur App Secret teeno bhar diye jaate hain, connection \"Connected\" dikhne lagta hai."),

        ("WhatsApp Setup",
            "Access token expire ho jaaye to kya hoga, kya refresh karna padega?",
            "Agar temporary (60-din wala) token use kiya hai, to platform automatically use refresh karta hai - expiry se 10 din pehle system apne aap naya token le leta hai (jab tak App ID aur App Secret sahi diya gaya ho). Sabse best tarika hai shuru mein hi ek System User (permanent) token banana - usme expiry hi nahi hoti."),

        ("WhatsApp Setup",
            "Inbound WhatsApp messages platform tak kaise pahunchte hain (webhook)?",
            "Ye ek shared webhook system hai - sabhi companies ke inbound messages ek hi platform-level Meta App ke through aate hain, aur system Phone Number ID dekhkar automatically pehchan leta hai ki message kis company ka hai. Aapko sirf apna App Secret dena hota hai taaki har incoming message ki signature verify ho sake (security ke liye)."),

        ("Lead Discovery",
            "Lead Discovery feature kaam kaise karta hai?",
            "Ye ek AI-powered feature hai jo internet par naye business leads dhoondhta hai. Aap ek profile set karte hain: target business type, keywords aur locations, batch size (ek run mein kitne leads chahiye), minimum lead score, aur phone/email required hai ya nahi. Iske baad AI web par real research karta hai, har candidate ko score deta hai, aur sirf qualifying leads CRM mein \"Discovered Lead\" ke roop mein save hote hain - duplicate ya reject hue candidates save hi nahi hote."),

        ("Lead Discovery",
            "Lead score (Hot/Warm/Cold) kaise decide hota hai?",
            "Har lead ko 0-100 ka score milta hai (business aapke target profile se kitna match karta hai):\n- 70 ya usse zyada = Hot\n- 40-69 = Warm\n- 40 se kam = Cold\n\nSirf wahi leads save hote hain jo profile mein set \"Minimum Score\" se upar hon."),

        ("Lead Discovery",
            "Discovered lead ko turant WhatsApp message kar sakte hain kya?",
            "Nahi. Naya lead discover hote hi \"Pending Opt-In\" status ke saath CRM mein add hota hai - jab tak koi manually us business ko opted-in mark na kare, tab tak use WhatsApp message nahi bheja ja sakta (WhatsApp ke policy compliance ke liye)."),

        ("AI Conversation",
            "AI conversation kaise kaam karta hai, aur kab human agent ko handoff hota hai?",
            "Har conversation ke 3 mode ho sakte hain: AI (har reply AI khud deta hai), Human (AI kabhi reply nahi karega, sirf agent hi respond karega), Hybrid (abhi ke version mein AI jaisa hi behave karta hai). AI apne confidence score ke hisaab se decide karta hai reply khud de ya agent ko bulaye - agar confidence 60% (0.6) se kam hai, ya customer ka intent kisi sensitive category (complaint, negotiation, complex technical issue, ya customer ne khud human maanga ho) mein aata hai, to conversation turant ek human agent ko handoff ho jaata hai."),

        ("AI Conversation",
            "Handoff hone ke kya reasons ho sakte hain?",
            "Handoff kai reasons se ho sakta hai:\n- Customer ne khud human se baat karne ko kaha\n- AI ka confidence score kam tha\n- AI ko jawab hi nahi pata\n- Customer complaint kar raha hai\n- Price/deal negotiation ho rahi hai\n- Koi complex technical sawaal hai\n- Ya AI-conversation quota khatam ho gayi (is case mein rule automatically apply hoti hai)"),

        ("Plans & Pricing",
            "Konse plans available hain?",
            "Teen plans hain:\n- Starter - $29/month, 3 users, 1,000 messages/month, 5 campaigns\n- Growth - $99/month, 10 users, 10,000 messages/month, 25 campaigns\n- Scale - $299/month, 50 users, 100,000 messages/month, 100 campaigns\n\nHar plan ke saath alag se WhatsApp/AI/Lead quota bhi milta hai jo har billing period mein renew hota hai."),

        ("Plans & Pricing",
            "Kya price sabhi countries mein same hoti hai?",
            "Base price USD mein set hai, lekin aapke country ke hisaab se automatically local currency mein convert ho jaati hai. Kuch countries ke liye platform ne khaas fixed price bhi set ki hui hai. India mein price ke upar 18% GST (tax) bhi add hota hai - agar aap platform ke registered state (Maharashtra) mein ho to CGST+SGST split hota hai, warna IGST laga hota hai.")
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        var existingQuestions = await db.FaqEntries.Select(f => f.Question).ToListAsync();
        var existingQuestionSet = new HashSet<string>(existingQuestions, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var displayOrder = 0;

        foreach (var spec in Catalog)
        {
            displayOrder++;

            if (existingQuestionSet.Contains(spec.Question))
                continue;

            db.FaqEntries.Add(new FaqEntry
            {
                Question = spec.Question,
                Answer = spec.Answer,
                Category = spec.Category,
                Status = ContentStatus.Published,
                DisplayOrder = displayOrder,
                CreatedByUserId = Guid.Empty
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }
}
