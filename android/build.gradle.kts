// Kotlin viene dentro del plugin de Android desde AGP 9: no hay que declarar
// org.jetbrains.kotlin.android (falla con «Cannot add extension with name kotlin»).
plugins {
    id("com.android.application") version "9.4.0" apply false
}
