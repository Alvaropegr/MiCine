import java.util.Properties

plugins {
    // Kotlin viene dentro del plugin de Android desde AGP 9.
    id("com.android.application")
}

/** La clave de firma vive en keystore.properties (no se comparte). Sin ella, se firma con la de depuración. */
val keystoreProps = Properties().apply {
    val f = rootProject.file("keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}

android {
    namespace = "es.micine.app"
    compileSdk = 36

    defaultConfig {
        applicationId = "es.micine.app"
        // 22 = Android 5.1: así funciona también en los Fire TV más antiguos (Fire OS 5 y 6).
        minSdk = 22
        targetSdk = 36
        versionCode = 4
        versionName = "1.3.0"
        androidResources.localeFilters += listOf("es")
    }

    // libVLC trae el reproductor compilado para cada procesador. Un APK por tipo pesa
    // la cuarta parte; el «universal» vale para cualquier móvil.
    splits {
        abi {
            isEnable = true
            reset()
            include("arm64-v8a", "armeabi-v7a", "x86_64")
            isUniversalApk = true
        }
    }

    signingConfigs {
        if (keystoreProps.getProperty("storeFile") != null) {
            create("release") {
                storeFile = rootProject.file(keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (keystoreProps.getProperty("storeFile") != null) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        // Las librerías nativas de VLC se cargan sin descomprimir.
        jniLibs.useLegacyPackaging = false
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    // El motor de VLC: reproduce prácticamente cualquier formato (MKV, AVI, WMV, AC3, DTS…).
    implementation("org.videolan.android:libvlc-all:3.7.7")

    // Pruebas de la lógica pura (catálogo, series, versiones) en el ordenador, sin emulador.
    testImplementation("junit:junit:4.13.2")
    // En las pruebas, el org.json de Android es solo de mentira: hace falta el de verdad.
    testImplementation("org.json:json:20240303")
}
