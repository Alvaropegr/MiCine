# libVLC llama desde código nativo (JNI) a sus clases Java: no se pueden renombrar ni quitar.
-keep class org.videolan.libvlc.** { *; }
-keep class org.videolan.medialibrary.** { *; }
-dontwarn org.videolan.**
