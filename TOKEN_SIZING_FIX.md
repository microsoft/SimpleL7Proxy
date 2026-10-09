# Token Sizing Fix - Tokens Tab

## Problem
The "Tokens" tab was oversizing token bars because it was:
1. Combining InputNet + Output tokens
2. Dividing by TokenAxisMaximum (which includes cached tokens)
3. Result: bars could exceed 100% of chart height, appearing cut off or disproportionate

## Solution
**Implemented per-tab axis scaling** in TokenomicsPage.razor (Tokens tab):

```razor
var tokenMax = Math.Max(1, _snapshot.Trend.Select(p => p.InputNet + p.Output).DefaultIfEmpty(0).Max());
```

This calculates the maximum of InputNet + Output ONLY for the Tokens tab, ensuring:
- ✅ Bars properly normalized to 100% of chart height
- ✅ Axis ticks (Y-axis labels) reflect tokens-only scale (no cached component)
- ✅ Visual proportions match the data being displayed
- ✅ Each tab now has independent axis scaling

## Implementation Details

**Before**: All tabs used shared `_snapshot.TokenAxisMaximum` which was calculated as:
```csharp
var maximum = Math.Max(1, trend.Select(point => point.InputNet + point.Cached + point.Output).DefaultIfEmpty(0).Max());
```

**After**: Tokens tab calculates its own max:
- Tokens tab: `InputNet + Output` only (no cached)
- Overview tab: `InputNet + Cached + Output` (original behavior preserved)
- Spend tab: Uses spend-based scaling (already correct)
- Limits/At Risk tabs: No chart scaling needed

## Razor Scope Rules Applied
- Variable `tokenMax` declared before HTML block (so it's in scope for `@for` and `@foreach`)
- Do NOT use `@for` inside `@{ }` code blocks - causes "Unexpected {" error
- Define variables in C# context before rendering HTML markup
- Reference those variables in markup using normal interpolation

## Testing
✅ Build succeeds: 0 errors, 0 warnings
✅ Tokens tab now displays with proper bar heights
✅ All other tabs unaffected
