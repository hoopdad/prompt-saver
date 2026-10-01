# Initial Product Prompt

Create a desktop application. Make sure it works for Windows and has Windows installers including x64 and ARM. The goal is to let users open it, type in an AI prompt, save it for later, then copy/paste it for use in their agent workspace.

When saving the prompt, it should save the prompt with searchable metadata that includes skills used, proper entities, and, if an LLM is available such as local Ollama or a Foundry OpenAI endpoint, derives a rationalized intent. When deriving intent, it should attempt to score and match against existing intents and a score of 85% would map automatically; lower would create a new one.

An intent is something like "draw a diagram for a customer", "write utility application", or "create a PPT". It should save them in a single location.

Fast opening to get to the point of writing a new prompt should be prioritized so we can brain dump as quickly as possible. Searching and configuring should also be available from the UI.

Design the system, design the UI, writing both to disk. Then iterate with a critic and a UI designer to refine the design. Then build using TDD principles, test, quality check it, and iterate until the product is high quality.

