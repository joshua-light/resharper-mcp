plugins {
    id("org.jetbrains.intellij.platform") version "2.2.1"
    kotlin("jvm") version "2.0.21"
}

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    intellijPlatform {
        rider("2025.3")
        instrumentationTools()
    }
}

kotlin {
    jvmToolchain(21)
}

tasks {
    buildSearchableOptions { enabled = false }
    patchPluginXml { enabled = false }
    verifyPluginProjectConfiguration { enabled = false }
    jar {
        archiveBaseName.set("ReSharperMcp")
        archiveClassifier.set("")
    }
}
