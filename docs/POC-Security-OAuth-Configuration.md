# POC: Security and OAuth 2.0 Configuration (Split Index)

**Purpose:** This index replaces the previous combined security POC and points to two focused runbooks.

## TL;DR

1. **Most important rule: use the doc that matches the security boundary you are configuring.**
2. Use the ACA document for client -> ACA authentication and authorization.
3. Use the APIM document for ACA -> APIM token validation and policy enforcement.

## Use these documents

- [POC-ACA-Proxy-Security-Authorization.md](POC-ACA-Proxy-Security-Authorization.md)
  - Focus: securing and authorizing the ACA proxy ingress.
  - Includes: ACA app registration, client app registration, ACA auth setup, and ACA validation tests.

- [POC-APIM-Security-Authorization.md](POC-APIM-Security-Authorization.md)
  - Focus: securing and authorizing APIM.
  - Includes: APIM app registration, ACA managed identity role assignment, APIM OAuth interface setup, `validate-jwt` policy, and APIM policy tests.

## Why the split

- ACA and APIM have different enforcement points and token audiences.
- Splitting reduces setup confusion and makes validation steps clearer.
- Each document now has a dedicated diagram, worked example, and verification checklist.
