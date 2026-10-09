# Tokenomics UI Tab Implementation

## Overview
Implemented fully functional tab-based UI views in the Tokenomics dashboard, allowing users to switch between five distinct data visualizations:
- **Overview**: Combined token usage and spend trend with cached input breakdown
- **Tokens**: Input and output tokens only (no cached or spend data)
- **Spend**: Daily spend trend as a bar chart
- **Limits**: Quota utilization percentages for tenants
- **At Risk**: Flagged and suspicious requests requiring attention

## Changes Made

### 1. TokenomicsDashboardStore.cs
**Extended TokenomicsDashboardSnapshot record** with new fields:
- `SpendTrend`: ImmutableArray<(string Date, decimal Spend)> - Pre-computed daily spend values
- `AtRiskRequests`: ImmutableArray<(int Rank, string User, string Model, string Reason, string Timestamp, string Status)> - Flagged requests

**Updated CreateSampleSnapshot()** to include:
- SpendTrend calculation from daily spend aggregates
- Sample at-risk data with three test cases (Blocked, Rejected, Sanitized)

### 2. TokenomicsEventReplay.cs
**Added SpendTrend calculation** (lines 280-285):
- Extracts daily spend values from aggregated trendByBucket data
- Paired with date labels for trend visualization

**Added AtRiskRequests extraction** (lines 299-318):
- Identifies requests where PolicyAction is "Reject", "Requeue", or ContentFilterReason exists
- Maps policy actions to human-readable status labels (Blocked, Throttled, Flagged)
- Takes top 10 requests, ranked by occurrence order
- Extracts user, model, reason, and timestamp for each flagged request

**Updated snapshot return statement** to include:
- `SpendTrend = spendTrend`
- `AtRiskRequests = atRiskRequests`

### 3. TokenomicsPage.razor

#### Tab Selection UI (lines 74-80)
Converted static span elements to functional buttons:
```html
<button type="button" class="@(_selectedViewTab == "Overview" ? "selected" : "")" 
        @onclick="@(() => SelectViewTab("Overview"))">Overview</button>
```
- Each tab is a clickable button
- Selected tab gets the "selected" CSS class
- OnClick handler calls SelectViewTab() with the tab name

#### Conditional Tab Content (lines 82-174)
Implemented five distinct views using `@if/@else if` blocks:

**Overview Tab** (lines 84-110)
- Shows combined token bars (input, cached, output) and spend line overlay
- Full legend with all four data types
- Original behavior preserved

**Tokens Tab** (lines 111-130)
- Shows input and output bars only (no cached, no spend)
- Combined InputNet + Output height for better proportional display
- Simplified legend with only input/output

**Spend Tab** (lines 131-152)
- Shows spend bars only (no token bars)
- Calculates max spend for normalized bar heights
- Single legend entry for spend

**Limits Tab** (lines 153-166)
- Displays quota utilization horizontal bars
- Shows tenant names and utilization percentages
- Empty state message if no quota data available

**At Risk Tab** (lines 167-192)
- Tabular view of flagged requests with columns:
  - Rank, User, Model, Reason, Timestamp, Status
- Status badges with conditional CSS classes (status-blocked, status-throttled, status-flagged)
- Empty state message if no flagged requests

#### Component State (@code block)
Added:
- `private string _selectedViewTab = "Overview"` - Tracks currently selected tab
- `SelectViewTab(string tab)` - Method to update selected tab and trigger re-render

## Data Flow

### Sample Data Path
1. CreateSampleSnapshot() generates randomized daily trends
2. SpendTrend calculated from daily spend aggregates
3. AtRiskRequests populated with sample flagged records
4. Store publishes snapshot
5. TokenomicsPage.razor receives snapshot and renders selected tab

### Live Event Path
1. EventHub delivers policy decisions and request outcomes
2. TokenomicsEventReplay.CreateSnapshot() parses events
3. Identifies at-risk requests based on rejection/requeue/content-filter
4. Aggregates spend by daily bucket
5. Returns snapshot with SpendTrend and AtRiskRequests
6. UI updates to display selected tab with live data

## CSS Classes Added
The following CSS classes are expected to exist or need to be defined:
- `.section-tabs button` - Tab button styling
- `.section-tabs button.selected` - Active tab indicator
- `.at-risk-table-wrap` - Container for at-risk requests table
- `.at-risk-table` - Table styling for flagged requests
- `.status-badge` - Badge container
- `.status-blocked` - Red badge for blocked requests
- `.status-throttled` - Orange badge for throttled requests
- `.status-flagged` - Yellow badge for flagged requests
- `.bar-spend` - Spend bar color in charts
- `.trend-bar` element children for Spend tab

## Testing Checklist
- [x] Build succeeds with 0 errors, 0 warnings
- [ ] Sample data loads and displays correctly in all tabs
- [ ] Tab selection updates UI dynamically
- [ ] Overview tab shows combined token + spend view
- [ ] Tokens tab shows input + output only (no cached/spend)
- [ ] Spend tab shows spend bars only
- [ ] Limits tab shows quota utilization bars
- [ ] At Risk tab displays flagged requests in table format
- [ ] Empty state messages appear when data unavailable
- [ ] Tab state persists across period selection changes
- [ ] Live EventHub data displays in all tabs

## Future Enhancements
1. Add tab persistence to URL query parameter (e.g., ?view=Tokens)
2. Implement export functionality for At Risk requests
3. Add filtering/sorting to At Risk table
4. Enhance Limits tab with per-model quota indicators
5. Add drill-down from At Risk requests to detailed event logs
6. Performance: Memoize tab content to avoid re-computing unchanged visualizations

## Notes
- AtRiskRequests are currently limited to top 10 records
- At Risk detection looks for policy rejections and content filter flags
- Status mapping: Reject→Blocked, Requeue→Throttled, other→Flagged
- SpendTrend uses same daily bucketing as Trend data for alignment
