using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Mobile.Controls
{
    public class RotateToggleAction : TriggerAction<VisualElement>
    {
        /// <summary>true = 开始旋转，false = 停止旋转</summary>
        public bool IsRunning { get; set; }

        private const string AnimationName = "RotateLoop";

        protected override void Invoke(VisualElement sender)
        {
            if (IsRunning)
                StartRotation(sender);
            else
                StopRotation(sender);
        }

        private void StartRotation(VisualElement sender)
        {
            // 防止重复启动
            sender.AbortAnimation(AnimationName);

            var animation = new Animation(
                callback: v => sender.Rotation = v,
                start: 0,
                end: 360,
                easing: Easing.Linear);

            animation.Commit(
                owner: sender,
                name: AnimationName,
                rate: 16,                          // 约 60 FPS
                length: 1000,                      // 一圈 1 秒
                repeat: () => IsRunning);          // 每圈结束检查是否继续
        }

        private void StopRotation(VisualElement sender)
        {
            sender.AbortAnimation(AnimationName);
            sender.Rotation = 0;                   // 复位到初始角度
        }       
    }
}
