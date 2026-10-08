plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

// Doit correspondre à la version de Godot utilisée pour l'export (Velom3d.csproj).
val godotVersion = "4.7.2.stable"
val pluginName = "velom3d_ble"

android {
    namespace = "org.velom3d.ble"
    compileSdk = 35

    defaultConfig {
        minSdk = 24
        manifestPlaceholders["godotPluginName"] = "Velom3dBle"
        manifestPlaceholders["godotPluginPackageName"] = namespace!!
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    // Fournie par le moteur à l'export : on compile contre, sans l'embarquer.
    compileOnly("org.godotengine:godot:$godotVersion")
}

// Copie les AAR là où l'export Godot les attend (addons/velom3d_ble/bin/).
val copyAars by tasks.registering(Copy::class) {
    dependsOn("assembleDebug", "assembleRelease")
    from(layout.buildDirectory.dir("outputs/aar"))
    include("*.aar")
    rename("plugin-(debug|release)\\.aar", "$pluginName-$1.aar")
    into(rootDir.resolve("../addons/$pluginName/bin"))
}

tasks.named("assemble") { finalizedBy(copyAars) }
