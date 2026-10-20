# 14-Day AI Platform Engineering Ramp-Up: Microsoft Foundry

**Objective:** Transition from traditional cloud infrastructure/DevOps to AI Platform Engineering by building a secure, governed, and observable AI Agent using the Microsoft Agent Framework Foundry Starter Pack (.NET).

**Core Repository:** [microsoft-agent-framework-foundry-starter-pack-net](https://github.com/Azure/microsoft-agent-framework-foundry-starter-pack-net)

---

## Phase 1: Architecture & Infrastructure as Code (Days 1-3)
**Theme:** Deploying and managing the lifecycle of an AI application.

*   **Day 1: Dissecting the 3-Tier Architecture.** 
    *   Clone the starter pack and map the architecture.
    *   Understand the separation of concerns: MCP server, Foundry-hosted agent, and the .NET Aspire cloud-native frontend.
*   **Day 2: Infrastructure as Code (Bicep/AZD).** 
    *   Review the `infra/` directory to see how the Azure Developer CLI (`azd`) provisions resources.
    *   Analyze the Bicep declarations for Azure AI Search, managed identities, and container apps. 
    *   *Interview Prep:* Be ready to explain how to translate this Bicep code into Terraform for a multi-cloud environment.
*   **Day 3: Local Orchestration with .NET Aspire.** 
    *   Run the application locally using `aspire run`.
    *   Review how Aspire wires up connection strings and networking between the Blazor frontend, agent backend, and MCP server.

---

## Phase 2: The Agentic Core & Data Integration (Days 4-7)
**Theme:** Securely connecting the LLM to internal tools and databases.

*   **Day 4: The Agent Framework.** 
    *   Study agent initialization via `Microsoft.Agents.AI.Foundry` and `Azure.AI.Projects`.
    *   Review how the Semantic Kernel acts as the middleware router.
*   **Day 5: Deep Dive into the MCP Server.** 
    *   Analyze the provided To-do list MCP server to see how schemas are exposed without giving the LLM code execution rights.
    *   *Action:* Modify the MCP server to return mock DevOps data (e.g., a fake Terraform state file or Kubernetes manifest).
*   **Day 6: Vector Database (RAG) Setup.** 
    *   Provision Azure AI Search.
    *   Ingest a mock IT security policy document.
    *   *Interview Prep:* Formulate an explanation differentiating Keyword, Vector, and Hybrid search routing.
*   **Day 7: Middleware Interceptors.** 
    *   Write a custom middleware script in the request pipeline that logs the token count of every request before it hits the LLM.

---

## Phase 3: Security & Enterprise Governance (Days 8-10)
**Theme:** Identity, access control, and human-in-the-loop governance.

*   **Day 8: Microsoft Entra ID & SSO.** 
    *   Configure an App Registration in Azure.
    *   Modify the Blazor frontend to require user authentication.
*   **Day 9: The On-Behalf-Of (OBO) Flow.** 
    *   Pass the logged-in user's Entra ID token through the agent to the MCP server.
    *   Ensure the tool only returns data that the specific user is authorized to view. *(Critical interview topic)*.
*   **Day 10: Human-in-the-Loop (HITL).** 
    *   Implement a pause in the orchestration workflow.
    *   Force the agent to wait for a human "Approve" click before mutating any data via the MCP server.

---

## Phase 4: Observability & CI/CD (Days 11-14)
**Theme:** Monitoring reasoning traces and deploying to production.

*   **Day 11: OpenTelemetry & Distributed Tracing.** 
    *   Connect the built-in OpenTelemetry framework to an Application Insights resource.
    *   *Action:* Generate a trace showing the exact latency breakdown between LLM generation time and MCP tool execution time.
*   **Day 12: Content Safety & Gateways.** 
    *   Route agent requests through Azure API Management (APIM) instead of directly hitting the Azure OpenAI endpoint.
    *   Configure rate limiting and a fallback routing policy.
*   **Day 13: CI/CD Pipelines.** 
    *   Write a GitHub Actions workflow to automate the `azd up` deployment process.
    *   Separate the deployment into `dev` and `prod` environments with different App Configuration endpoints.
*   **Day 14: Mock Interview & Documentation.** 
    *   Create an architectural diagram of the finished system.
    *   Write out "STAR" (Situation, Task, Action, Result) stories based on this build.
    *   Draft your opinionated stance on why separating the MCP server from the main application pod is a critical security boundary.