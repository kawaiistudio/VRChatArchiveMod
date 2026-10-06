#pragma once
#include <string>
#include <string_view>

// A feature of the mod. Kept deliberately small: the C# mod's modules carried a lot of framework
// weight; here a module is a name, an on/off state, and the few callbacks the engine drives. More
// callbacks (player join/leave, scene load) are added to this base as the engine grows.
namespace VRCA {

    class Module {
    public:
        explicit Module(std::string_view name, std::string_view desc = "")
            : m_name(name), m_desc(desc) {}
        virtual ~Module() = default;

        [[nodiscard]] std::string_view Name() const { return m_name; }
        [[nodiscard]] std::string_view Desc() const { return m_desc; }
        [[nodiscard]] bool Enabled() const { return m_enabled; }

        void SetEnabled(bool on) {
            if (m_enabled == on) return;
            m_enabled = on;
            if (on) OnEnable(); else OnDisable();
        }

        // Ticked once per frame by the native pump, only while enabled.
        virtual void OnUpdate() {}

        // AFTER UNITY HAS ANIMATED, every frame, only while enabled.
        //
        // Update runs BEFORE the Animator evaluates. Anything written to a humanoid bone there is
        // overwritten by the avatar's own animator a few milliseconds later, in the same frame, so
        // it never reaches the screen or the network -- which is exactly why the pose copy looked
        // like it did nothing at all. The C# mod applied its pose from LateUpdate for this reason;
        // this is that phase, and every feature that fights Unity's own animation belongs in it.
        virtual void OnLateUpdate() {}
        virtual void OnEnable() {}
        virtual void OnDisable() {}

    protected:
        std::string m_name;
        std::string m_desc;
        bool        m_enabled = false;
    };
}
