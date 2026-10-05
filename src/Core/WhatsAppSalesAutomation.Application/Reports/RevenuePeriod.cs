namespace WhatsAppSalesAutomation.Application.Reports;

/// <summary>
/// The window the Revenue report and the marketing comparison share: whole calendar months ending now, so "3 months"
/// is the first day of the month two months back through today. Kept in one place so the two always agree.
/// </summary>
public readonly record struct RevenuePeriod(int Months, DateTime From, DateTime To)
{
    public static RevenuePeriod Resolve(int months, DateTime utcNow)
    {
        months = Math.Clamp(months, ReportService.MinMonths, ReportService.MaxMonths);
        var from = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(months - 1));
        return new RevenuePeriod(months, from, utcNow);
    }

    /// <summary>One bucket per day for a one-month period, otherwise one per month - the same buckets the revenue trend uses.</summary>
    public IReadOnlyList<DateTime> BucketStarts()
    {
        var starts = new List<DateTime>();
        if (Months == 1)
        {
            for (var day = From; day.Date <= To.Date; day = day.AddDays(1))
                starts.Add(day.Date);
            return starts;
        }

        for (var i = 0; i < Months; i++)
            starts.Add(From.AddMonths(i));
        return starts;
    }

    /// <summary>The bucket a moment falls in, matching <see cref="BucketStarts"/>.</summary>
    public DateTime BucketOf(DateTime moment) =>
        Months == 1 ? moment.Date : new DateTime(moment.Year, moment.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
