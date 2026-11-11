# Purchase Requisition (PR) Creation Process: As-Is vs. To-Be (Agentic AI)

---

## 1. As-Is Process Flow (Manual & Administrative Heavy)

| Step | Process Name | Responsible Role | Step Description & Details |
| :--- | :--- | :--- | :--- |
| **1** | **PR Request Submission** | **Requester** *(Human)* | • **IT Users:** Submit request via Purchase Request Tracker v2.0.<br>• **Non-IT Users:** Submit request manually via Email. |
| **2** | **Intake Verification & Follow-up** | **PR Admin** *(Human)* | • Manually reviews submitted data to ensure quality, completeness, and accuracy.<br>• Manually follows up via email/chat with requesters whenever mandatory data is missing or inaccurate. |
| **3** | **Manual SAP PR Creation** | **PR Admin** *(Human)* | • PR Admin logs into SAP (T-code `ME51N`).<br>• Manually maps backend SAP fields (Account Assignment Category, G/L Account, Purchasing Group, Item Category, WBS/Cost Center) and enters data to create the PR. |
| **4** | **Notification & Tracking Update** | **PR Admin** *(Human)* | • Manually sends email notification to the requester with the generated PR number.<br>• Manually records the SAP PR number into the PR Tracker spreadsheet. |
| **5** | **Managerial Approval** | **Approver** *(Human)* | • Approver reviews and approves the PR in SAP according to the Manual of Authority (MOA) financial threshold. |
| **6** | **Procurement Execution** | **Procurement Team** *(Human)* | • Procurement executes purchasing/sourcing activities once the PR is fully released in SAP. |

---

## 2. To-Be Process Flow (Agentic AI & Guided Workflow)

| Step | Process Name | Responsible Role | Step Description & Details |
| :--- | :--- | :--- | :--- |
| **1** | **Guided PR Intake & Suggestion** | **Requester + Agentic AI** *(Human + AI)* | • User fills a **Guided PR Form** equipped with dynamic dropdowns, auto-filled fields, and cascading logic.<br>• **AI Agent Integration:** Agent assists the user by analyzing intent, suggesting correct budget types (OPEX vs. CAPEX), validating real-time SAP budget availability, and proposing PR auto-splits (if mixing materials & services). |
| **2** | **Agentic Validation & Sign-off** | **PR Reviewer / Section Manager** *(Human)* | • Reviewer inspects the AI-generated PR draft and validation check results.<br>• Clicks **"Approve / Proceed to SAP"** to trigger automated creation. |
| **3** | **Automated SAP PR Creation** | **SAP MCP / API** *(Automated)* | • The PR is automatically created in SAP via Model Context Protocol (MCP) / Direct SAP APIs using the validated structured payload.<br>• Eliminates manual data entry completely. |
| **4** | **Auto Notification & Dashboard Sync** | **System Automation** *(Automated)* | • Automatically emails the requester and approval line with the SAP PR number upon creation.<br>• Automatically logs the SAP PR number into the central PR Tracker Dashboard. |
| **5** | **Managerial Approval** | **Approver** *(Human - Unchanged)* | • Standard SAP approval workflow runs per MOA threshold (*Kept as-is*). |
| **6** | **Procurement Execution** | **Procurement Team** *(Human - Unchanged)* | • Standard procurement execution starts once the PR is released (*Kept as-is*). |

---

## 3. Key Transformation & Business Value Summary

| Process Area | As-Is (Baseline) | To-Be (Agentic AI) | Core Business Impact |
| :--- | :--- | :--- | :--- |
| **Data Intake** | Unstructured email / manual tracker entry | Guided UI with cascading fields & AI suggestions | Eliminates initial user error & back-and-forth communication |
| **Validation** | Human PR Admin manual review | Real-time AI agent validation & SAP budget check | Instant budget remaining warnings before submission |
| **SAP Creation** | Manual typing into SAP (`ME51N`) | Automated PR creation via SAP MCP API | Reduces PR creation time from days/hours to seconds |
| **Tracking** | Manual spreadsheet logging | Real-time automated dashboard update | Full end-to-end auditability and visibility |
| **PR Splitting** | Done manually by PR Admin | Automatically suggested by AI agent | Ensures compliance with SAP split rules (Item Type, Purchasing Group, Budget Type) |