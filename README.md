# AI-Driven Text RPG Engine

**An experimental engine for procedural world generation, simulation, and narrative interaction powered by Multi-Agent Systems.**

![Status](https://img.shields.io/badge/Status-Work_in_Progress-yellow) ![Tech](https://img.shields.io/badge/AI-LLM_Agents-blue) ![Engine](https://img.shields.io/badge/UI-Godot-green)

## 📖 Overview

This project is a research sandbox designed to explore advanced interactions between Autonomous AI Agents within a game development context. It is not just a text quest; it is a complex engine where an infinite coordinate-based world (X/Y) is generated, simulated, and visualized in real-time by a symbiosis of specialized AI models.

The core goal is to demonstrate how LLMs can handle logic, state management, and creative writing simultaneously by splitting tasks among distinct algorithmic agents.

## ✨ Key Features

### 🧠 Advanced AI Architecture
*   **Natural Language Interface:** A single entry point (Godot-based UI) where user input is analyzed for syntax and intent. The system automatically determines the necessary task pipeline without rigid command structures.
*   **Hybrid Model Support:** Flexible backend that connects to various AI providers (including **Google Gemini**) and **Local LLMs**.
*   **Unfiltered Narrative Control:** A delegation system that assigns specific generation steps to local models, allowing for unrestricted storytelling that might be flagged by the safety filters of large cloud providers.

### 🌍 World Generation & Data
*   **Infinite Spatial Grid:** The world is an endless map of fixed-size spatial cells (indices X/Y).
*   **Procedural Entity Creation:** Locations and objects are generated based on custom descriptions and stored as hierarchical JSON structures. Entities interact with each other based on their spatial index.
*   **Vector-Based Memory:** Utilizes **Embeddings** and vector search to retrieve relevant context and data from the database, effectively implementing RAG (Retrieval-Augmented Generation) for game lore.
*   **Git-Like State Management:** Game data is stored with a versioning system similar to Git, supporting patches and version history for world states.

### ⚙️ Deep Simulation
*   **Multi-Step Event Processing:**
    1.  User commands are parsed.
    2.  The engine calculates new states for objects and locations.
    3.  An algorithmic action list is created for the scene.
    4.  A narrative agent writes a literary description of the events.
*   **Autonomous Agent Symbiosis:** The system separates roles into distinct components—**Search**, **Simulation**, and **Generation**. These agents act in symbiosis to drive the game world forward, often without direct user intervention.

### 🎨 Visualization
*   **Text-to-Visual Rendering:** The engine interprets the textual and JSON-based world state to render 2D visualizations (PNG) via the Godot engine, bridging the gap between raw data and visual feedback.

## 🛠️ Tech Stack & Concepts
*   **Core Logic:** С# / LLM Integration
*   **UI & Visualization:** Godot Engine
*   **AI Concepts:** RAG (Vector Search), Chain-of-Thought, Multi-Agent Orchestration.
*   **Data Structure:** JSON, Hierarchical Indexing.

## 🚀 Roadmap

*   [ ] **UI Overhaul:** Modernizing the interface for better UX.
*   [ ] **Z-Axis Support:** Introducing verticality to the spatial grid.
*   [ ] **Long-Distance Travel:** Implementing algorithms for simulating transitions across thousands of cells in a single simulation step.
