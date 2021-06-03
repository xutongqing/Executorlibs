namespace Executorlibs.Shared.Protocol.Models.Danmaku
{
    /// <summary>
    /// 全平台通用的勋章基本信息接口
    /// </summary>
    public interface IMedal
    {
        /// <summary>
        /// 名称
        /// </summary>
        string Name { get; }
        /// <summary>
        /// 等级
        /// </summary>
        int Level { get; }
        /// <summary>
        /// 所属房间号
        /// </summary>
        int RoomId { get; }
        /// <summary>
        /// 所属主播昵称
        /// </summary>
        string Master { get; }
        /// <summary>
        /// 所属主播UID
        /// </summary>
        object MasterId { get; }
        /// <summary>
        /// 颜色
        /// </summary>
        int Color { get; }
    }

    /// <summary>
    /// 全平台通用的勋章基本信息接口
    /// </summary>
    /// <typeparam name="TUserId">用户Id的类型</typeparam>
    public interface IMedal<TUserId> : IMedal
    {
        object IMedal.MasterId => MasterId!;
        /// <summary>
        /// 所属主播UID
        /// </summary>
        new TUserId MasterId { get; }
    }
}